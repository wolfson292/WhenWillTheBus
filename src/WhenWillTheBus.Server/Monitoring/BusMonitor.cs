// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.Options;
using WhenWillTheBus.Core;
using WhenWillTheBus.Core.Api;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Prediction;
using WhenWillTheBus.Core.Storage;
using WhenWillTheBus.Server.Cleanup;
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
    TraceCleaner cleaner,
    IOptions<MonitorOptions> options,
    ILogger<BusMonitor> logger) : BackgroundService
{
    private readonly MonitorOptions _options = options.Value;
    private readonly Dictionary<long, long> _lastServerTime = [];

    private Dictionary<long, Student> _students = [];
    private DateTimeOffset _rosterRefreshedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _scansPolledAt = DateTimeOffset.MinValue;

    // Read off disk at startup and held until the roster names each rider.
    // Students do not exist until then, so there is nowhere else to put them.
    private Dictionary<long, IReadOnlyList<ScanEvent>> _storedScans = [];

    // Who each id with history belonged to, as last seen. Outlives the roster
    // on purpose: it is how a rider who reappears under a new id is recognised.
    private readonly Dictionary<long, string?> _names = [];

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

            _storedScans = stored.ToDictionary(rider => rider.ChildId, rider => rider.Scans);
            foreach (StoredRider rider in stored)
            {
                _names[rider.ChildId] = rider.Name;
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

        // Only between runs: tidying last week's traces must never hold up a
        // poll while a bus is on its way.
        if (!busy && await cleaner.PassAsync(_students.Keys, now, token).ConfigureAwait(false))
        {
            await SaveHistoryAsync(token, "cleanup").ConfigureAwait(false);
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

        fresh = CarryScans(fresh, _students, _storedScans);

        // BEFORE the roster replaces the old one, while the old id's scans are
        // still to hand. On 27 Oct WheresTheBus gave Willow a new id overnight;
        // everything here is keyed by it, so she became a rider with no past,
        // and the next save wrote the file without the old one -- 49 runs gone,
        // and both apps had nothing to predict from that morning.
        List<(long From, long To)> moves = Renumbered(fresh, _names);
        foreach ((long from, long to) in moves)
        {
            engine.Renumber(from, to);

            IReadOnlyList<ScanEvent> older = _students.TryGetValue(from, out Student? previous)
                ? previous.Scans
                : _storedScans.GetValueOrDefault(from) ?? [];
            fresh[to] = fresh[to] with
            {
                Scans = [.. older
                    .Concat(fresh[to].Scans)
                    .GroupBy(scan => scan.Timestamp)
                    .Select(group => group.Last())
                    .OrderBy(scan => scan.Timestamp)
                    .TakeLast(HistoryStore.ScanHistoryLimit)],
            };

            _storedScans.Remove(from);
            _names.Remove(from);
            logger.LogWarning("Rider {From} is now {To}; moved their history across", from, to);
        }

        foreach (Student student in fresh.Values)
        {
            _names[student.ChildId] = student.Name;
        }

        _students = fresh;
        logger.LogInformation("Roster: {Riders}", string.Join(", ", fresh.Values.Select(rider => rider.Name)));

        if (moves.Count > 0)
        {
            await SaveHistoryAsync(token, "a rider renumbered").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Which riders with history now appear on the roster under another id.
    /// </summary>
    /// <remarks>
    /// Matched by name. A file from before names were kept has none, and then
    /// only the unambiguous case is taken -- one rider gone, one on the roster
    /// -- because moving one child's history onto another's would be worse
    /// than leaving it where it is.
    /// </remarks>
    public static List<(long From, long To)> Renumbered(
        IReadOnlyDictionary<long, Student> roster,
        IReadOnlyDictionary<long, string?> known)
    {
        List<long> gone = [.. known.Keys.Where(id => !roster.ContainsKey(id))];
        List<(long From, long To)> moves = [];

        foreach (long from in gone)
        {
            string? name = known[from]?.Trim();
            List<long> matches = name is null
                ? (gone.Count == 1 && roster.Count == 1 ? [.. roster.Keys] : [])
                : [.. roster.Values
                    .Where(student => string.Equals(student.Name.Trim(), name, StringComparison.OrdinalIgnoreCase))
                    .Select(student => student.ChildId)];

            if (matches.Count == 1)
            {
                moves.Add((from, matches[0]));
            }
        }

        return moves;
    }

    /// <summary>
    /// Give a freshly read roster the scans already known for each rider.
    /// </summary>
    /// <remarks>
    /// The endpoint only ever returns the current day, so a roster refresh must
    /// not drop what has accumulated -- and the FIRST refresh after a restart
    /// has nothing in memory to carry, so it takes what was on disk. Without
    /// that, the scans were saved and never read back: the first poll after
    /// every restart wrote today's scans over the whole history, and on
    /// 26 Oct the file held two.
    /// </remarks>
    public static Dictionary<long, Student> CarryScans(
        Dictionary<long, Student> fresh,
        IReadOnlyDictionary<long, Student> current,
        IReadOnlyDictionary<long, IReadOnlyList<ScanEvent>> stored)
    {
        foreach ((long childId, Student student) in fresh)
        {
            if (current.TryGetValue(childId, out Student? existing))
            {
                fresh[childId] = student with { Scans = existing.Scans };
            }
            else if (stored.TryGetValue(childId, out IReadOnlyList<ScanEvent>? scans))
            {
                fresh[childId] = student with { Scans = scans };
            }
        }

        return fresh;
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
        // Every rider with anything learned, not just today's roster: a rider
        // the roster stops naming keeps their history on disk, where a later
        // renumbering can still find it.
        List<StoredRider> riders = _students.Keys
            .Union(engine.Riders)
            .Union(_storedScans.Keys)
            .Select(childId => new StoredRider
            {
                ChildId = childId,
                Name = _names.GetValueOrDefault(childId),
                Arrivals = engine.ArrivalsFor(childId),
                Scans = _students.TryGetValue(childId, out Student? student)
                    ? student.Scans
                    : _storedScans.GetValueOrDefault(childId) ?? [],
            })
            .ToList();

        await HistoryStore.SaveAsync(_options.HistoryPath, riders, token).ConfigureAwait(false);
        logger.LogInformation("History saved ({Why})", why);
    }
}
