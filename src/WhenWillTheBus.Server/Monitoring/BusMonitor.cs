// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.Options;
using WhenWillTheBus.Core;
using WhenWillTheBus.Core.Api;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Prediction;
using WhenWillTheBus.Core.Storage;
using WhenWillTheBus.Server.Devices;
using WhenWillTheBus.Server.LiveActivity;

namespace WhenWillTheBus.Server.Monitoring;

/// <summary>
/// The always-on component the handoff says you realistically need.
/// </summary>
/// <remarks>
/// Home Assistant polled every thirty seconds forever; an iOS app does not run.
/// This does the polling, runs the prediction, and pushes Live Activity updates
/// to whichever phones are showing the journey — the one channel that reliably
/// reaches a suspended app.
///
/// The prediction itself is <see cref="PredictionEngine"/>, byte-for-byte the
/// same code the phone runs. That is the whole reason the engine lives in a
/// platform-agnostic library: two implementations of this algorithm would
/// disagree, and the disagreement would show up as a child on a kerb.
/// </remarks>
public sealed class BusMonitor(
    WheresTheBusClient client,
    PredictionEngine engine,
    LiveActivityPublisher publisher,
    DeviceRegistry registry,
    LocalClock clock,
    IOptions<MonitorOptions> options,
    ILogger<BusMonitor> logger) : BackgroundService
{
    private readonly MonitorOptions _options = options.Value;
    private readonly Dictionary<long, long> _lastServerTime = [];

    private Dictionary<long, Student> _students = [];
    private DateTimeOffset _rosterRefreshedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _scansPolledAt = DateTimeOffset.MinValue;

    /// <summary>The riders currently known, for the status endpoint.</summary>
    public IReadOnlyDictionary<long, Student> Students => _students;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await LoadAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan wait = _options.IdlePoll;

            try
            {
                wait = await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (WheresTheBusAuthException error)
            {
                // Credentials, not connectivity. Retrying every thirty seconds
                // will not fix it and may get the account locked.
                logger.LogError(error, "WheresTheBus rejected the credentials; backing right off");
                wait = TimeSpan.FromMinutes(15);
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "Poll failed; will try again");
                wait = _options.IdlePoll;
            }

            try
            {
                await Task.Delay(wait, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task LoadAsync(CancellationToken token)
    {
        Directory.CreateDirectory(_options.DataDirectory);
        await registry.LoadAsync(_options.RegistryPath, token).ConfigureAwait(false);

        try
        {
            IReadOnlyList<StoredRider> stored = await HistoryStore
                .LoadAsync(_options.HistoryPath, token).ConfigureAwait(false);

            foreach (StoredRider rider in stored)
            {
                engine.LoadHistory(rider.ChildId, rider.Arrivals);
            }

            logger.LogInformation("Loaded history for {Count} rider(s)", stored.Count);
        }
        catch (Exception error) when (error is InvalidDataException or IOException)
        {
            logger.LogError(error, "Could not read stored history; starting with none");
        }
    }

    /// <summary>One poll. Returns how long to wait before the next one.</summary>
    private async Task<TimeSpan> TickAsync(CancellationToken token)
    {
        DateTimeOffset now = clock.Now;

        if (now - _rosterRefreshedAt >= _options.RosterRefresh || _students.Count == 0)
        {
            await RefreshRosterAsync(token).ConfigureAwait(false);
            _rosterRefreshedAt = now;
        }

        if (_students.Count == 0)
        {
            logger.LogWarning("No riders on this account yet");
            return _options.IdlePoll;
        }

        if (now - _scansPolledAt >= _options.ScanPoll)
        {
            await RefreshScansAsync(token).ConfigureAwait(false);
            _scansPolledAt = now;
        }

        bool anyWatched = false;

        foreach (Student student in _students.Values.ToList())
        {
            if (student.BusNumber is null)
            {
                continue;
            }

            bool watched = engine.IsWatching(student, Run.Am, now) || engine.IsWatching(student, Run.Pm, now);
            anyWatched |= watched;

            // Outside every window there is nothing this reading could change,
            // and the service belongs to somebody else.
            if (!watched)
            {
                continue;
            }

            RiderInfo info = await client.GetRiderInfoAsync(
                student.BusNumber,
                student.ChildId,
                _lastServerTime.GetValueOrDefault(student.ChildId),
                token).ConfigureAwait(false);

            _lastServerTime[student.ChildId] = info.ServerTime;

            engine.Observe(student, info, now);

            ArrivalPrediction? prediction = engine.PredictNextArrival(student, now);

            // What the morning bar fills TOWARDS. §5's rule 3 shows the ride
            // with or without it, so a null here costs the countdown, not the
            // stage -- which is the whole point, because this is learned from
            // the drop-off scans that end the very ride it describes.
            SchoolArrival? school = SchoolArrivalPredictor.Predict(student, now, clock);
            Journey journey = engine.Stage(student, now, prediction, school?.Arrival);

            await publisher
                .PublishAsync(student, journey, prediction, info, now, token)
                .ConfigureAwait(false);
        }

        if (engine.PromotePending(_students, now))
        {
            await SaveHistoryAsync(token).ConfigureAwait(false);
        }

        if (registry.Prune(now) > 0)
        {
            await registry.SaveAsync(token).ConfigureAwait(false);
        }

        return anyWatched ? _options.BusPoll : _options.IdlePoll;
    }

    private async Task RefreshRosterAsync(CancellationToken token)
    {
        using System.Text.Json.JsonDocument userInfo = await client.GetUserInfoAsync(token).ConfigureAwait(false);
        using System.Text.Json.JsonDocument allRiders = await client.GetAllRidersAsync(token).ConfigureAwait(false);

        Dictionary<long, Student> fresh = RosterReader.ReadRoster(
            userInfo.RootElement.GetProperty("payload"),
            allRiders.RootElement.GetProperty("payload"));

        // Carry across the scans already accumulated: the endpoint only ever
        // returns the current day, so a roster refresh must not drop them.
        foreach ((long childId, Student student) in fresh)
        {
            if (_students.TryGetValue(childId, out Student? existing))
            {
                fresh[childId] = student with { Scans = existing.Scans };
            }
        }

        _students = fresh;
        logger.LogInformation("Roster: {Riders}", string.Join(", ", fresh.Values.Select(rider => rider.Name)));
    }

    private async Task RefreshScansAsync(CancellationToken token)
    {
        using System.Text.Json.JsonDocument scans = await client.GetStudentScansAsync(token).ConfigureAwait(false);

        Dictionary<long, List<ScanEvent>> byChild = RosterReader.ReadScans(
            scans.RootElement.GetProperty("payload"), _students);

        foreach ((long childId, List<ScanEvent> fresh) in byChild)
        {
            if (!_students.TryGetValue(childId, out Student? student))
            {
                continue;
            }

            // Merge into what is already known BEFORE classifying, because the
            // alternation fallback needs to see a whole day, and because the
            // endpoint only ever returns today.
            List<ScanEvent> merged = student.Scans
                .Concat(fresh)
                .GroupBy(scan => scan.Timestamp)
                .Select(group => group.First())
                .OrderBy(scan => scan.Timestamp)
                .TakeLast(HistoryStore.ScanHistoryLimit)
                .ToList();

            _students[childId] = student with
            {
                Scans = ScanClassifier.Classify(merged, student.SchoolName, clock),
            };
        }
    }

    /// <summary>Persist what has been learned. Public so an import can write through.</summary>
    public Task PersistAsync(CancellationToken token = default) => SaveHistoryAsync(token);

    private async Task SaveHistoryAsync(CancellationToken token)
    {
        List<StoredRider> riders = _students.Keys
            .Select(childId => new StoredRider
            {
                ChildId = childId,
                Arrivals = engine.ArrivalsFor(childId),
                Scans = _students[childId].Scans,
            })
            .ToList();

        await HistoryStore.SaveAsync(_options.HistoryPath, riders, token).ConfigureAwait(false);
        logger.LogInformation("Learned a new arrival; history saved");
    }
}
