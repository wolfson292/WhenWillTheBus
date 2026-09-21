// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core.Api;
using WhenWillTheBus.Core.Model;

namespace WhenWillTheBus.Core.Prediction;

/// <summary>
/// Predicts when the bus next reaches a rider's stop, learns from every arrival
/// it watches, and keeps the published answer still enough to be worth reading.
/// </summary>
/// <remarks>
/// Three bases, tried strictly in order — route, then the anchor ladder, then
/// the clock. The first that applies wins.
///
/// Before any of them: ASK ONCE whether this run has already happened today,
/// and if so skip it entirely. That was fixed once in the historical branch
/// alone and came straight back through the route branch the moment it was
/// added — on 17 Sep the bus reached the stop at 08:04 and the estimate went on
/// predicting the morning arrival for five more minutes, drifting later each
/// poll. Asked in one place that all three share, it cannot be forgotten by
/// whichever branch is added next.
/// </remarks>
public sealed class PredictionEngine(LocalClock clock)
{
    private readonly record struct RunKey(long ChildId, Run Run, DateOnly Day);

    private readonly Dictionary<long, List<RunArrival>> _arrivals = [];
    private readonly Dictionary<RunKey, ApproachRecorder> _approach = [];
    private readonly Dictionary<RunKey, (double ClosestMiles, DateTimeOffset When)> _pending = [];
    private readonly Dictionary<RunKey, DateTimeOffset> _published = [];
    private readonly Dictionary<(long ChildId, Run Run), (TimeOnly Earliest, TimeOnly Latest)> _bounds = [];
    private readonly Dictionary<long, RiderInfo> _latest = [];

    /// <summary>
    /// What was predicted, and when, for each run being watched.
    /// </summary>
    /// <remarks>
    /// Kept so an arrival can record how wrong the estimate was, which is the
    /// only way to know whether this port behaves like the one it replaces.
    /// Bounded: an approach is watched for about 75 minutes at 30-second polls,
    /// so a couple of hundred entries is the whole of it.
    /// </remarks>
    private readonly Dictionary<RunKey, List<(DateTimeOffset At, DateTimeOffset Predicted)>> _predicted = [];

    private const int PredictionLogLimit = 256;

    public LocalClock Clock { get; } = clock;

    /// <summary>Every arrival learned for a rider, oldest first.</summary>
    public IReadOnlyList<RunArrival> ArrivalsFor(long childId) =>
        _arrivals.TryGetValue(childId, out List<RunArrival>? history) ? history : [];

    /// <summary>The most recent reading folded in for a rider.</summary>
    public RiderInfo? LatestFor(long childId) => _latest.GetValueOrDefault(childId);

    /// <summary>
    /// Load history, merging with anything already known.
    /// </summary>
    /// <remarks>
    /// Predictions work from TWO route samples and three arrivals for outlier
    /// rejection, so a fortnight of imported history makes the app useful on day
    /// one rather than in October.
    /// </remarks>
    public void LoadHistory(long childId, IEnumerable<RunArrival> arrivals)
    {
        List<RunArrival> merged = Merge(ArrivalsFor(childId), arrivals);
        _arrivals[childId] = TrimPerRun(merged);
    }

    // ---------------------------------------------------------------- reading

    /// <summary>Fold one live reading into whichever run is currently being watched.</summary>
    public void Observe(Student student, RiderInfo info, DateTimeOffset now)
    {
        _latest[student.ChildId] = info;

        if (info.DistanceMiles is null)
        {
            return;
        }

        // Anything but a CURRENT fix means the distance behind it was
        // extrapolated, which is what the stale count records.
        FoldReading(
            student,
            now,
            info.DistanceMiles.Value,
            info.Bus,
            fresh: info.Status == BusStatusKind.Current);
    }

    /// <summary>
    /// Fold one distance reading, live or replayed, into its run.
    /// </summary>
    /// <remarks>
    /// <paramref name="when"/> is a parameter rather than the clock because a
    /// restart has to replay the part of a run it missed, and a replayed reading
    /// has to land exactly where the live one would have.
    /// </remarks>
    public void FoldReading(
        Student student,
        DateTimeOffset when,
        double distanceMiles,
        GeoPoint? position = null,
        bool fresh = true)
    {
        DateOnly day = Clock.DateOf(when);

        foreach (Run run in (ReadOnlySpan<Run>)[Run.Am, Run.Pm])
        {
            if (!IsWatching(student, run, when))
            {
                continue;
            }

            RunKey key = new(student.ChildId, run, day);
            if (!_approach.TryGetValue(key, out ApproachRecorder? recorder))
            {
                recorder = new ApproachRecorder();
                _approach[key] = recorder;
            }

            recorder.Sample(
                Tuning.AnchorLadderMiles,
                when,
                distanceMiles,
                Tuning.ArrivalThresholdMiles,
                position,
                fresh);

            // Only readings inside the TIGHTER arrival window can count as the
            // arrival itself.
            (DateTimeOffset Start, DateTimeOffset End)? arriving =
                RunWindows.Arrival(Clock, student.ScheduledFor(run), LearnedTime(student.ChildId, run).Time, when);

            if (!RunWindows.IsOpen(arriving, when))
            {
                continue;
            }

            // FIRST INSIDE, not closest. A bus dwelling at the stop gives two or
            // three readings under the threshold, and "closest" lands on
            // whichever poll had the best fix — so the recorded arrival, and
            // every leg measured back from it, carried a poll of noise for no
            // reason. Until something is inside, the closest so far still
            // stands, so a run where the bus never arrives is still judged on
            // how near it got.
            if (!_pending.TryGetValue(key, out (double ClosestMiles, DateTimeOffset When) best)
                || (best.ClosestMiles > Tuning.ArrivalThresholdMiles && distanceMiles < best.ClosestMiles))
            {
                _pending[key] = (distanceMiles, when);
            }
        }
    }

    /// <summary>
    /// Whether this run's route is worth recording at this instant: the approach
    /// window, plus the whole ride once a scan says the rider is aboard.
    /// </summary>
    /// <remarks>
    /// The ride home starts twenty to thirty minutes before the approach window
    /// opens, and that stretch is exactly what a route estimate needs: it is
    /// where a skipped stop shows up as being further along than usual.
    /// </remarks>
    public bool IsWatching(Student student, Run run, DateTimeOffset now)
    {
        (DateTimeOffset Start, DateTimeOffset End)? window =
            RunWindows.Approach(Clock, student.ScheduledFor(run), LearnedTime(student.ChildId, run).Time, now);

        if (window is null)
        {
            return false;
        }

        if (RunWindows.IsOpen(window, now))
        {
            return true;
        }

        ScanEvent? pickup = student.LastScanOf(ScanKind.Pickup);
        if (pickup is null)
        {
            return false;
        }

        DateTimeOffset boarded = pickup.Timestamp;
        return Clock.DateOf(boarded) == Clock.DateOf(now)
            && Clock.RunOf(boarded) == run
            && boarded <= now
            && now <= window.Value.End;
    }

    // ------------------------------------------------------------- predicting

    /// <summary>Predict when the bus next reaches this rider's stop.</summary>
    public ArrivalPrediction? PredictNextArrival(Student student, DateTimeOffset now)
    {
        List<ArrivalPrediction> candidates = [];

        foreach (Run run in (ReadOnlySpan<Run>)[Run.Am, Run.Pm])
        {
            TimeOnly? scheduled = student.ScheduledFor(run);
            if (scheduled is null)
            {
                continue;
            }

            (TimeOnly? learned, int samples, int? spread, int outliers) = LearnedTime(student.ChildId, run);

            // Asked ONCE, here, for all three bases.
            bool arrived = AlreadyArrived(student.ChildId, run, Clock.DateOf(now));

            // 1. Where the bus has got to along the route beats both the rung
            //    ladder and the clock, because it is the only one of the three
            //    that reconsiders on every position report.
            RouteEstimate? onRoute = RouteArrival(student, run, now);
            if (onRoute is not null && !arrived && IsWatching(student, run, now))
            {
                candidates.Add(new ArrivalPrediction
                {
                    Run = run,
                    Arrival = onRoute.Value.Arrival,
                    Source = PredictionSource.Learned,
                    Basis = PredictionBasis.Route,
                    Samples = samples,
                    SpreadMinutes = spread,
                    Outliers = outliers,
                    Scheduled = scheduled,
                    RouteSamples = onRoute.Value.Seen,
                    Earliest = onRoute.Value.Earliest,
                    Latest = onRoute.Value.Latest,
                    Centre = learned ?? scheduled,
                });
                continue;
            }

            // 2. Failing that: once inside a rung, when the bus set off no
            //    longer matters. Anchor to the crossing and stop guessing.
            AnchorEstimate? anchored = AnchoredArrival(student.ChildId, run, now);
            if (anchored is not null && !arrived)
            {
                candidates.Add(new ArrivalPrediction
                {
                    Run = run,
                    Arrival = anchored.Value.Arrival,
                    Source = PredictionSource.Learned,
                    Basis = PredictionBasis.Approach,
                    Samples = samples,
                    SpreadMinutes = spread,
                    Outliers = outliers,
                    Scheduled = scheduled,
                    AnchoredAtMiles = anchored.Value.RungMiles,
                    AnchorSamples = anchored.Value.Samples,
                    Earliest = anchored.Value.Earliest,
                    Latest = anchored.Value.Latest,
                    Centre = learned ?? scheduled,
                });
                continue;
            }

            // 3. The clock. The median of what this run has actually done,
            //    falling back to the timetable while nothing has been learned.
            TimeOnly predicted = learned ?? scheduled.Value;
            HashSet<int> service = ServiceDays(student.ChildId);

            for (int offset = 0; offset < Tuning.DaysAhead; offset++)
            {
                DateOnly day = Clock.DateOf(now).AddDays(offset);
                DateTimeOffset moment = Clock.AtLocal(day, predicted);

                // A time that has gone by still counts while today's window is
                // open and the bus has not come. Rolling on to tomorrow at that
                // point is what made the afternoon of 15 Sep flap: the estimate
                // kept landing a few minutes ahead, the clock caught up, the
                // prediction jumped to tomorrow morning, the journey collapsed
                // to idle — and the next rung crossing started it all again.
                bool overdue = offset == 0 && StillDue(student, run, now);

                if ((moment > now || overdue)
                    && service.Contains(MondayIsZero(day.DayOfWeek))
                    && !AlreadyArrived(student.ChildId, run, day))
                {
                    _bounds.TryGetValue((student.ChildId, run), out (TimeOnly Earliest, TimeOnly Latest) span);
                    bool haveSpan = _bounds.ContainsKey((student.ChildId, run));

                    candidates.Add(new ArrivalPrediction
                    {
                        Run = run,
                        Arrival = moment,
                        Earliest = haveSpan ? Clock.AtLocal(day, span.Earliest) : null,
                        Latest = haveSpan ? Clock.AtLocal(day, span.Latest) : null,
                        Source = learned is not null ? PredictionSource.Learned : PredictionSource.Scheduled,
                        Basis = learned is not null ? PredictionBasis.Historical : PredictionBasis.Scheduled,
                        Samples = samples,
                        SpreadMinutes = spread,
                        Outliers = outliers,
                        Scheduled = scheduled,
                        Centre = learned ?? scheduled,
                    });
                    break;
                }
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        ArrivalPrediction soonest = candidates.MinBy(candidate => candidate.Arrival)!;
        ArrivalPrediction published = Steady(student.ChildId, soonest);

        Remember(student.ChildId, published, now);
        return published;
    }

    /// <summary>Note what was published, so the arrival can be scored against it.</summary>
    private void Remember(long childId, ArrivalPrediction published, DateTimeOffset now)
    {
        // Only while the prediction is about today. Once it rolls on to the next
        // school day it says nothing about the run being watched.
        if (Clock.DateOf(published.Arrival) != Clock.DateOf(now))
        {
            return;
        }

        RunKey key = new(childId, published.Run, Clock.DateOf(now));
        if (!_predicted.TryGetValue(key, out List<(DateTimeOffset At, DateTimeOffset Predicted)>? log))
        {
            log = [];
            _predicted[key] = log;
        }

        log.Add((now, published.Arrival));

        if (log.Count > PredictionLogLimit)
        {
            log.RemoveRange(0, log.Count - PredictionLogLimit);
        }
    }

    /// <summary>
    /// How wrong the estimate was, scored against an arrival that has happened.
    /// </summary>
    /// <remarks>
    /// Scored at the moment a parent would act on it, not at the moment it was
    /// most accurate. Reporting the best number the estimate ever produced would
    /// flatter it; five minutes out is when somebody decides to walk to the kerb.
    /// </remarks>
    private (int? AtFiveMinutes, int? AtArrival) Score(RunKey key, DateTimeOffset arrival)
    {
        if (!_predicted.TryGetValue(key, out List<(DateTimeOffset At, DateTimeOffset Predicted)>? log)
            || log.Count == 0)
        {
            return (null, null);
        }

        List<(DateTimeOffset At, DateTimeOffset Predicted)> before =
            log.Where(entry => entry.At <= arrival).ToList();

        if (before.Count == 0)
        {
            return (null, null);
        }

        int? final = (int)(before[^1].Predicted - arrival).TotalSeconds;

        // The last estimate standing at least five minutes out. Not the nearest
        // to that instant in either direction: one published four minutes before
        // arrival knows things a parent leaving at five minutes did not.
        DateTimeOffset horizon = arrival.AddMinutes(-5);
        (DateTimeOffset At, DateTimeOffset Predicted)? atFive = before
            .Where(entry => entry.At <= horizon)
            .Cast<(DateTimeOffset At, DateTimeOffset Predicted)?>()
            .LastOrDefault();

        int? five = atFive is null ? null : (int)(atFive.Value.Predicted - arrival).TotalSeconds;
        return (five, final);
    }

    private readonly record struct RouteEstimate(
        DateTimeOffset Arrival, int Seen, DateTimeOffset Earliest, DateTimeOffset Latest);

    /// <summary>Estimate arrival from where the bus is on its route.</summary>
    private RouteEstimate? RouteArrival(Student student, Run run, DateTimeOffset now)
    {
        RiderInfo? info = _latest.GetValueOrDefault(student.ChildId);
        if (info?.Bus is not GeoPoint bus)
        {
            return null;
        }

        // Nothing is reporting, so the last position is not a claim about where
        // the bus is, at any age.
        if (info.Status == BusStatusKind.Inactive)
        {
            return null;
        }

        // WHEN this position was true, which is not necessarily now.
        //
        // A stale reading is the last known fix repeated, so measuring the
        // remaining time forward from the CLOCK adds a second of predicted
        // lateness for every second the feed stays frozen — the estimate sliding
        // with the clock, which is the exact failure this whole approach exists
        // to avoid, arriving through a door nobody was watching. A six-minute
        // freeze walked the arrival six minutes later, then snapped back on thaw.
        //
        // Anchoring to the fix instead assumes the bus carried on at the pace
        // past journeys kept from there. That is the honest guess about a stretch
        // nobody watched, and it is also the SAFER one: pushing the arrival later
        // is what puts a child on the kerb after the bus has gone.
        DateTimeOffset observed = info.GpsAgeMinutes is > 0
            ? now - TimeSpan.FromMinutes(info.GpsAgeMinutes.Value)
            : now;

        int? elapsed = null;
        double? heading = null;

        if (_approach.TryGetValue(new RunKey(student.ChildId, run, Clock.DateOf(now)), out ApproachRecorder? journey)
            && journey.Track.Count > 0)
        {
            elapsed = (int)(observed - journey.Track[0].When).TotalSeconds;

            // Which way the bus is going right now, so a past sample taken on
            // the other side of a U-turn cannot claim to be where it is.
            List<GeoPoint> sofar = journey.Track.Select(point => new GeoPoint(point.Latitude, point.Longitude)).ToList();
            sofar.Add(bus);
            heading = Geo.HeadingOf(sofar);
        }

        List<int> remainders = [];
        foreach (RunArrival past in ArrivalsFor(student.ChildId))
        {
            if (past.Run != run || past.Substitute || past.Track.Count == 0)
            {
                continue;
            }

            int? left = RouteMatcher.NearestRemaining(
                past.Track, bus.Latitude, bus.Longitude, elapsed, RouteMatcher.MatchRadiusMiles, heading);

            if (left is not null)
            {
                remainders.Add(left.Value);
            }
        }

        if (remainders.Count < RouteMatcher.MinimumRouteSamples)
        {
            return null;
        }

        remainders.Sort();
        (IReadOnlyList<int> usual, _) = Statistics.RejectOutliers(
            remainders, Statistics.OutlierFloorMinutes * 60);
        int middle = Statistics.Median(usual);

        DateTimeOffset estimate = observed.AddSeconds(middle);
        if (estimate <= now)
        {
            // By this reckoning the bus is already due, which after a long freeze
            // it may well be. Saying nothing lets a basis anchored to a known
            // instant answer instead.
            return null;
        }

        // Agreement among a handful of past journeys is not certainty.
        double floor = Tuning.RouteBandFloor.TotalSeconds;
        return new RouteEstimate(
            estimate,
            remainders.Count,
            observed.AddSeconds(Math.Min(usual[0], middle - floor)),
            observed.AddSeconds(Math.Max(usual[^1], middle + floor)));
    }

    private readonly record struct AnchorEstimate(
        DateTimeOffset Arrival, double RungMiles, int Samples, DateTimeOffset Earliest, DateTimeOffset Latest);

    /// <summary>
    /// Estimate arrival from the live approach: the tightest rung the bus has
    /// actually been WATCHED crossing, plus the typical leg from it.
    /// </summary>
    private AnchorEstimate? AnchoredArrival(long childId, Run run, DateTimeOffset now)
    {
        if (!_approach.TryGetValue(new RunKey(childId, run, Clock.DateOf(now)), out ApproachRecorder? approach)
            || approach.Crossings.Count == 0)
        {
            return null;
        }

        // Tightest rung first: the closer the bus was when it crossed, the less
        // of the journey is left to vary. Higher index means a smaller distance.
        List<int> legs = [];
        int chosen = -1;

        foreach (int rung in approach.Crossings.Keys.OrderDescending())
        {
            legs = ArrivalsFor(childId)
                .Where(past => past.Run == run && !past.Substitute && past.Legs.ContainsKey(rung))
                .Select(past => past.Legs[rung])
                .Order()
                .ToList();

            if (legs.Count > 0)
            {
                chosen = rung;
                break;
            }
        }

        if (chosen < 0)
        {
            return null;
        }

        DateTimeOffset crossed = approach.Crossings[chosen];
        (IReadOnlyList<int> kept, _) = Statistics.RejectOutliers(legs, Statistics.OutlierFloorMinutes * 60);
        DateTimeOffset estimate = crossed.AddSeconds(Statistics.Median(kept));

        // A bus already overdue against this estimate has arrived, or is about
        // to; leave it be rather than reporting a time in the past.
        if (estimate <= now)
        {
            return null;
        }

        return new AnchorEstimate(
            estimate,
            Tuning.AnchorLadderMiles[chosen],
            kept.Count,
            crossed.AddSeconds(kept[0]),
            crossed.AddSeconds(kept[^1]));
    }

    /// <summary>
    /// Hold the published arrival still unless the estimate really moved.
    /// </summary>
    /// <remarks>
    /// THE HOLD IS THE BAND, not a fixed two minutes. A flat tolerance is wrong
    /// at both ends: far out it is narrower than the real uncertainty and
    /// republishes noise, and ninety seconds from the stop it is WIDER than the
    /// whole band, so the figure shown can sit outside the range shown beside it.
    ///
    /// The band is NOT shifted to match the held arrival. Dragging it to agree
    /// with a held number makes it say something that has not been measured —
    /// the band is your statement of what you know, and it should say it.
    ///
    /// Keyed per rider, run and date. A shared key lets one rider's wobble pin
    /// another's.
    /// </remarks>
    private ArrivalPrediction Steady(long childId, ArrivalPrediction prediction)
    {
        RunKey key = new(childId, prediction.Run, Clock.DateOf(prediction.Arrival));

        if (_published.TryGetValue(key, out DateTimeOffset held) && CloseEnough(prediction, held))
        {
            return prediction with { Arrival = held };
        }

        // Keep only this date's, so this cannot grow without bound.
        foreach (RunKey stale in _published.Keys.Where(candidate => candidate.Day != key.Day).ToList())
        {
            _published.Remove(stale);
        }

        _published[key] = prediction.Arrival;
        return prediction;
    }

    /// <summary>Whether a held arrival still sits inside what is being claimed.</summary>
    private static bool CloseEnough(ArrivalPrediction prediction, DateTimeOffset held)
    {
        double drift = Math.Abs((prediction.Arrival - held).TotalSeconds);
        if (drift <= Tuning.ArrivalHysteresisFloor.TotalSeconds)
        {
            return true;
        }

        if (prediction.Earliest is null || prediction.Latest is null)
        {
            return drift <= Tuning.ArrivalHysteresis.TotalSeconds;
        }

        return prediction.Earliest <= held && held <= prediction.Latest;
    }

    // ------------------------------------------------------------- what we know

    /// <summary>
    /// The typical arrival for a run, its spread, and how many days were cut.
    /// </summary>
    /// <remarks>
    /// Badly late days are discarded before the median is taken. The median
    /// already resists them, but they would still widen the reported spread and,
    /// if several accumulated, drag the prediction — so they are excluded from
    /// the calculation while REMAINING IN HISTORY.
    /// </remarks>
    public (TimeOnly? Time, int Samples, int? SpreadMinutes, int Outliers) LearnedTime(long childId, Run run)
    {
        List<int> times = ArrivalsFor(childId)
            .Where(past => past.Run == run && !past.Substitute)
            .Select(past =>
            {
                DateTime local = Clock.ToLocal(past.Arrival);
                return (local.Hour * 60) + local.Minute;
            })
            .Order()
            .ToList();

        if (times.Count == 0)
        {
            return (null, 0, null, 0);
        }

        (IReadOnlyList<int> kept, int excluded) = Statistics.RejectOutliers(times);
        int middle = Statistics.Median(kept);
        int spread = kept[^1] - kept[0];

        _bounds[(childId, run)] = (
            new TimeOnly(kept[0] / 60, kept[0] % 60),
            new TimeOnly(kept[^1] / 60, kept[^1] % 60));

        return (new TimeOnly(middle / 60, middle % 60), kept.Count, spread, excluded);
    }

    /// <summary>
    /// Whether the bus has already reached this stop for that run that day.
    /// </summary>
    /// <remarks>
    /// An arrival is not written to history the moment it happens: it is
    /// promoted when its window closes, half an hour or so after the bus has
    /// been and gone. So the PENDING record has to be read too, or every
    /// prediction between the bus arriving and the window shutting still counts
    /// today's run as yet to come — which is what left the morning estimate
    /// re-widening from one minute to fourteen at 07:58 on 15 Sep, a minute
    /// after the bus pulled up.
    /// </remarks>
    public bool AlreadyArrived(long childId, Run run, DateOnly day)
    {
        RunKey key = new(childId, run, day);
        if (_pending.TryGetValue(key, out (double ClosestMiles, DateTimeOffset When) pending)
            && pending.ClosestMiles <= Tuning.ArrivalThresholdMiles)
        {
            return true;
        }

        return ArrivalsFor(childId).Any(past => past.Run == run && Clock.DateOf(past.Arrival) == day);
    }

    /// <summary>
    /// When this afternoon's bus reached the stop, if it has.
    /// </summary>
    /// <remarks>
    /// Read from the pending arrival as well as the promoted ones, because an
    /// arrival is not written to history until its window shuts — half an hour
    /// after the rider is already indoors, which is far too late to be the thing
    /// that ends the journey.
    /// </remarks>
    public DateTimeOffset? RideHomeEnded(long childId, DateTimeOffset now)
    {
        DateOnly day = Clock.DateOf(now);

        if (_pending.TryGetValue(new RunKey(childId, Run.Pm, day), out (double ClosestMiles, DateTimeOffset When) pending)
            && pending.ClosestMiles <= Tuning.ArrivalThresholdMiles)
        {
            return pending.When;
        }

        return ArrivalsFor(childId)
            .Where(past => past.Run == Run.Pm && Clock.DateOf(past.Arrival) == day)
            .Select(past => (DateTimeOffset?)past.Arrival)
            .FirstOrDefault();
    }

    /// <summary>
    /// Whether today's run is past its expected time but has not been yet. The
    /// bus is late, not cancelled, and until its window shuts it is still the
    /// next time the bus comes.
    /// </summary>
    public bool StillDue(Student student, Run run, DateTimeOffset now)
    {
        (DateTimeOffset Start, DateTimeOffset End)? window =
            RunWindows.Arrival(Clock, student.ScheduledFor(run), LearnedTime(student.ChildId, run).Time, now);

        return window is not null && now <= window.Value.End;
    }

    /// <summary>
    /// The weekdays this rider's bus is believed to run: weekdays by default,
    /// plus any day an arrival has actually been seen on.
    /// </summary>
    /// <remarks>
    /// Learned rather than hard-coded, so a route that genuinely runs at the
    /// weekend keeps working once it has been observed doing so. The default
    /// cannot be inferred the other way round: a rider who happens not to have
    /// ridden on a Wednesday yet must not lose Wednesdays.
    /// </remarks>
    public HashSet<int> ServiceDays(long childId)
    {
        HashSet<int> days = [.. Enumerable.Range(0, Tuning.SchoolWeek)];
        foreach (RunArrival past in ArrivalsFor(childId))
        {
            days.Add(MondayIsZero(Clock.ToLocal(past.Arrival).DayOfWeek));
        }

        return days;
    }

    /// <summary>Which stage of the run this rider is in right now.</summary>
    public Journey Stage(Student student, DateTimeOffset now, ArrivalPrediction? prediction, DateTimeOffset? schoolArrival)
    {
        RiderInfo? info = _latest.GetValueOrDefault(student.ChildId);
        Run clockRun = Clock.RunOf(now);

        (DateTimeOffset Start, DateTimeOffset End)? approach = RunWindows.Approach(
            Clock, student.ScheduledFor(clockRun), LearnedTime(student.ChildId, clockRun).Time, now);

        return JourneyStageMachine.Evaluate(
            new StageInputs
            {
                Now = now,
                DistanceMiles = info?.DistanceMiles,
                NextArrival = prediction?.Arrival,
                NextRun = prediction?.Run,
                PredictionSource = prediction?.Source,
                SchoolArrival = schoolArrival,
                LastPickup = student.LastScanOf(ScanKind.Pickup)?.Timestamp,
                LastDropoff = student.LastScanOf(ScanKind.Dropoff)?.Timestamp,
                ApproachOpen = RunWindows.IsOpen(approach, now),
                RideHomeEnded = RideHomeEnded(student.ChildId, now),
            },
            Clock);
    }

    // --------------------------------------------------------------- learning

    /// <summary>
    /// Turn closed windows into arrivals. Returns whether anything changed, so
    /// the caller knows to persist.
    /// </summary>
    public bool PromotePending(IReadOnlyDictionary<long, Student> students, DateTimeOffset now)
    {
        bool changed = false;

        // The approach is watched over a wider window than the arrival, so a run
        // can have a recorded approach and no pending arrival at all — a day
        // nobody was collected. Both have to be swept.
        foreach (RunKey key in _pending.Keys.Union(_approach.Keys).ToList())
        {
            if (!students.TryGetValue(key.ChildId, out Student? student))
            {
                _pending.Remove(key);
                _approach.Remove(key);
                continue;
            }

            (DateTimeOffset Start, DateTimeOffset End)? window = RunWindows.Arrival(
                Clock, student.ScheduledFor(key.Run), LearnedTime(key.ChildId, key.Run).Time, now);

            bool stillOpen = window is not null && key.Day == Clock.DateOf(now) && now <= window.Value.End;
            if (stillOpen)
            {
                continue;
            }

            _approach.Remove(key, out ApproachRecorder? approach);
            bool hadPending = _pending.Remove(key, out (double ClosestMiles, DateTimeOffset When) pending);

            // A run where the bus never really came — nobody to collect, or a
            // cancelled route — must not be learned as an arrival time.
            if (!hadPending || pending.ClosestMiles > Tuning.ArrivalThresholdMiles)
            {
                continue;
            }

            approach ??= new ApproachRecorder();
            (IReadOnlyDictionary<int, int> legs, IReadOnlyList<TrackPoint> track, int recedes, int stale) =
                approach.Finish(pending.When);

            (int? errorAtFive, int? errorAtArrival) = Score(key, pending.When);
            _predicted.Remove(key);

            List<RunArrival> history = _arrivals.TryGetValue(key.ChildId, out List<RunArrival>? existing)
                ? existing
                : [];

            history.Add(new RunArrival
            {
                Run = key.Run,
                Arrival = pending.When,
                ClosestMiles = pending.ClosestMiles,
                Substitute = student.SubstituteBus is not null,
                Legs = legs,
                Track = track,
                Recedes = recedes,
                Stale = stale,
                Boarded = BoardingFor(student, key.Run, key.Day),
                ErrorAtFiveMinutes = errorAtFive,
                ErrorAtArrival = errorAtArrival,
            });

            history.Sort((left, right) => left.Arrival.CompareTo(right.Arrival));
            _arrivals[key.ChildId] = TrimPerRun(history);
            changed = true;
        }

        return changed;
    }

    private DateTimeOffset? BoardingFor(Student student, Run run, DateOnly day) =>
        student.Scans
            .Where(scan => scan.Kind == ScanKind.Pickup
                && Clock.DateOf(scan.Timestamp) == day
                && Clock.RunOf(scan.Timestamp) == run)
            .Select(scan => (DateTimeOffset?)scan.Timestamp)
            .LastOrDefault();

    /// <summary>
    /// Combine two sets of arrivals, keeping whichever record knows more.
    /// </summary>
    /// <remarks>
    /// A bus reaches a given stop once per run per day, so that PAIR identifies
    /// an arrival — not its timestamp, which differs by microseconds between the
    /// live path and a replay of the same reading. Treating those as separate
    /// arrivals let duplicates accumulate, each counting towards the sample
    /// total, and let a replay carrying a fuller record be discarded as already
    /// known.
    ///
    /// Whether history has been read for an arrival is a property of the
    /// ARRIVAL, not of whichever record won, so it survives from either side.
    /// </remarks>
    private List<RunArrival> Merge(IEnumerable<RunArrival> existing, IEnumerable<RunArrival> fresh)
    {
        Dictionary<(Run, DateOnly), RunArrival> byRunDay = [];

        foreach (RunArrival item in existing.Concat(fresh))
        {
            (Run, DateOnly) key = (item.Run, Clock.DateOf(item.Arrival));
            if (!byRunDay.TryGetValue(key, out RunArrival? current))
            {
                byRunDay[key] = item;
                continue;
            }

            RunArrival winner = Compare(item.Knows, current.Knows) > 0 ? item : current;
            byRunDay[key] = winner with { Replayed = item.Replayed || current.Replayed };
        }

        return byRunDay.Values.OrderBy(item => item.Arrival).ToList();

        static int Compare((int Legs, int Track) left, (int Legs, int Track) right) =>
            left.Legs != right.Legs ? left.Legs.CompareTo(right.Legs) : left.Track.CompareTo(right.Track);
    }

    /// <summary>Keep the most recent arrivals of EACH run, counted separately.</summary>
    private static List<RunArrival> TrimPerRun(List<RunArrival> history)
    {
        List<RunArrival> kept = [];

        foreach (Run run in (ReadOnlySpan<Run>)[Run.Am, Run.Pm])
        {
            List<RunArrival> matching = history.Where(item => item.Run == run).ToList();
            if (matching.Count > Tuning.ArrivalHistoryLimit)
            {
                matching = matching[^Tuning.ArrivalHistoryLimit..];
            }

            kept.AddRange(matching);
        }

        kept.Sort((left, right) => left.Arrival.CompareTo(right.Arrival));
        return kept;
    }

    /// <summary>Monday as 0, matching the weekday numbering the ladder was learned with.</summary>
    private static int MondayIsZero(DayOfWeek day) => ((int)day + 6) % 7;
}
