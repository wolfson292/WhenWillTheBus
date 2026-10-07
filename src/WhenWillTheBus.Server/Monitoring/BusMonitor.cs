// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.Options;
using WhenWillTheBus.Core;
using WhenWillTheBus.Core.Api;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Prediction;
using WhenWillTheBus.Core.Storage;
using WhenWillTheBus.Server.Devices;
using WhenWillTheBus.Server.Media;
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
    RunAlerter alerter,
    DeviceRegistry registry,
    ClientRegistry clients,
    RequestLog requests,
    ReleaseTracker releases,
    IOptions<MediaOptions> media,
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
        await clients.LoadAsync(_options.ClientsPath, token).ConfigureAwait(false);
        await requests.LoadAsync(_options.RequestsPath, media.Value.HistoryLimit, token)
            .ConfigureAwait(false);
        await releases.LoadAsync(_options.ReleasePath, token).ConfigureAwait(false);

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
            // PERSISTED WHEN THEY CHANGE, not as a side effect of learning an
            // arrival. Saving only on promotion meant a scan arriving after the
            // day's last arrival was never written -- and a restart put the
            // history back to whatever had been. Across a week of redeploys
            // that left exactly one scan on disk, so the school-arrival
            // estimate never had more than a fragment of a day to work from and
            // the morning ride had no target at 08:01, which is the only time
            // it matters.
            if (await RefreshScansAsync(token).ConfigureAwait(false))
            {
                await SaveHistoryAsync(token, "scans").ConfigureAwait(false);
            }

            _scansPolledAt = now;
        }

        bool busy = false;

        foreach (Student student in _students.Values.ToList())
        {
            if (student.BusNumber is null)
            {
                continue;
            }

            // WATCHING THE BUS AND HAVING A JOURNEY TO SHOW ARE DIFFERENT
            // QUESTIONS, and answering both with this one cost a whole morning
            // ride. This window is built around the arrival at the RIDER'S
            // STOP and closes thirty minutes after it -- but the morning
            // journey BEGINS at that arrival and runs for another hour to the
            // school. On 29 Sep the pickup was at 07:59 and this closed at
            // 08:31, so the Lock Screen card stopped being updated 36% of the
            // way to school, sat frozen there, and was never even cleared; the
            // app, which evaluates the stage on every request, showed the same
            // ride correctly to the end.
            bool watched = engine.IsWatching(student, Run.Am, now) || engine.IsWatching(student, Run.Pm, now);

            // Only the READING is gated. Outside every window there is nothing
            // it could change, and the service belongs to somebody else.
            RiderInfo? info = null;
            if (watched)
            {
                info = await client.GetRiderInfoAsync(
                    student.BusNumber,
                    student.ChildId,
                    _lastServerTime.GetValueOrDefault(student.ChildId),
                    token).ConfigureAwait(false);

                _lastServerTime[student.ChildId] = info.ServerTime;

                engine.Observe(student, info, now);
            }

            ArrivalPrediction? prediction = engine.PredictNextArrival(student, now);

            // What the morning bar fills TOWARDS. §5's rule 3 shows the ride
            // with or without it, so a null here costs the countdown, not the
            // stage -- which is the whole point, because this is learned from
            // the drop-off scans that end the very ride it describes.
            SchoolArrival? school = SchoolArrivalPredictor.Predict(student, now, clock);
            Journey journey = engine.Stage(student, now, prediction, school?.Arrival);

            // BEFORE the publisher, so a card that hears "the bus has set off"
            // or "aboard, home around 5:30" hears it with the alert rather than
            // as a silent refresh first. This is what reaches a phone nobody
            // has opened today, morning or afternoon.
            await alerter
                .AlertAsync(student, journey, prediction, info, now, token)
                .ConfigureAwait(false);

            // Deliberately NOT the stale reading: a card claiming a distance
            // and a fix time from half an hour ago is worse than one that
            // admits it has neither. The aboard stages draw a bar from elapsed
            // time and never needed it.
            await publisher
                .PublishAsync(student, journey, prediction, info, now, token)
                .ConfigureAwait(false);

            // A journey in progress is reason enough to keep the fast cadence:
            // the push backstop is five minutes and a card goes visibly stale
            // two minutes after that, so polling this rider at the idle rate
            // would leave the ride to school flickering between fresh and
            // stale for an hour. Nothing here calls WheresTheBus unless the
            // rider is also watched.
            busy |= watched || journey.Active;
        }

        if (engine.PromotePending(_students, now))
        {
            await SaveHistoryAsync(token, "a new arrival").ConfigureAwait(false);
        }

        if (registry.Prune(now) > 0)
        {
            await registry.SaveAsync(token).ConfigureAwait(false);
        }

        if (clients.Prune(now) > 0)
        {
            await clients.SaveAsync(token).ConfigureAwait(false);
        }

        return busy ? _options.BusPoll : _options.IdlePoll;
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

    /// <summary>Poll the scans. Returns whether anything actually changed.</summary>
    private async Task<bool> RefreshScansAsync(CancellationToken token)
    {
        bool changed = false;

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

            // Compared BEFORE classifying: the classifier can relabel an
            // existing scan as a whole day comes into view, and a relabelling
            // is worth persisting too.
            changed |= merged.Count != student.Scans.Count;

            _students[childId] = student with
            {
                Scans = ScanClassifier.Classify(merged, student.SchoolName, clock),
            };
        }

        return changed;
    }

    /// <summary>Persist what has been learned. Public so an import can write through.</summary>
    public Task PersistAsync(CancellationToken token = default) => SaveHistoryAsync(token, "asked to");

    private async Task SaveHistoryAsync(CancellationToken token, string why)
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
        logger.LogInformation("History saved ({Why})", why);
    }
}
