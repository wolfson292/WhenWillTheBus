// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core.Api;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Prediction;

namespace WhenWillTheBus.Core.Tests;

public sealed class PredictionEngineTests
{
    private static PredictionEngine WithTwoPastMornings()
    {
        PredictionEngine engine = new(EngineFixtures.Clock);
        engine.LoadHistory(
            EngineFixtures.ChildId,
            [
                EngineFixtures.PastMorning(EngineFixtures.LocalAt(2026, 9, 15, 8, 0)),
                EngineFixtures.PastMorning(EngineFixtures.LocalAt(2026, 9, 16, 8, 2)),
            ]);

        return engine;
    }

    /// <summary>
    /// §11.2, and the most dangerous bug in the catalogue. The route matcher read
    /// the live position without checking freshness. During a feed freeze it
    /// matched the same frozen position every poll, got the same remaining time,
    /// and published it as that many minutes from a clock that kept advancing —
    /// so a six-minute freeze walked the arrival six minutes LATER, then snapped
    /// back on thaw.
    ///
    /// Note the direction of the error: pushing the arrival later is what leaves
    /// a child on the kerb after the bus has gone.
    /// </summary>
    [Fact]
    public void FrozenFeed_DoesNotWalkTheArrivalLater()
    {
        PredictionEngine engine = WithTwoPastMornings();
        GeoPoint parked = EngineFixtures.WhereItWasWith(600);

        DateTimeOffset live = EngineFixtures.LocalAt(2026, 9, 17, 7, 50);
        engine.Observe(EngineFixtures.MorningOnly, EngineFixtures.Reading(parked, 2.0), live);
        ArrivalPrediction? fresh = engine.PredictNextArrival(EngineFixtures.MorningOnly, live);

        Assert.NotNull(fresh);
        Assert.Equal(PredictionBasis.Route, fresh.Basis);

        // Six minutes later the feed has frozen: the SAME position, now reported
        // as six minutes old. The bus has not been seen to move, so the estimate
        // must not move either.
        DateTimeOffset frozen = live.AddMinutes(6);
        engine.Observe(
            EngineFixtures.MorningOnly,
            EngineFixtures.Reading(parked, 2.0, BusStatusKind.Stale, ageMinutes: 6),
            frozen);

        ArrivalPrediction? stale = engine.PredictNextArrival(EngineFixtures.MorningOnly, frozen);

        Assert.NotNull(stale);
        Assert.Equal(fresh.Arrival, stale.Arrival);
    }

    /// <summary>
    /// §2. An inactive feed is not a claim about where the bus is at any age, so
    /// the route basis declines rather than matching a position nobody stands
    /// behind.
    /// </summary>
    [Fact]
    public void InactiveFeed_DoesNotCarryARouteEstimate()
    {
        PredictionEngine engine = WithTwoPastMornings();
        DateTimeOffset now = EngineFixtures.LocalAt(2026, 9, 17, 7, 50);

        engine.Observe(
            EngineFixtures.MorningOnly,
            EngineFixtures.Reading(EngineFixtures.WhereItWasWith(600), 2.0, BusStatusKind.Inactive, null),
            now);

        ArrivalPrediction? prediction = engine.PredictNextArrival(EngineFixtures.MorningOnly, now);

        Assert.NotNull(prediction);
        Assert.NotEqual(PredictionBasis.Route, prediction.Basis);
    }

    /// <summary>
    /// §11.1 and §3.1. The bus drives AWAY from the stop constantly — serving
    /// other children, turning in cul-de-sacs. A distance-indexed estimate reads
    /// that as a setback and slides forward with the clock. A route-matched one
    /// converges, because the position still says where on the route it is.
    /// </summary>
    [Fact]
    public void EstimateConverges_AsTheBusWorksItsWayAlongTheRoute()
    {
        PredictionEngine engine = WithTwoPastMornings();
        DateTimeOffset start = EngineFixtures.LocalAt(2026, 9, 17, 7, 45);

        List<double> minutesOut = [];
        for (int poll = 0; poll < 8; poll++)
        {
            DateTimeOffset now = start.AddSeconds(poll * 30);
            int remaining = 900 - (poll * 30);

            engine.Observe(
                EngineFixtures.MorningOnly,
                EngineFixtures.Reading(EngineFixtures.WhereItWasWith(remaining), 2.0),
                now);

            ArrivalPrediction? prediction = engine.PredictNextArrival(EngineFixtures.MorningOnly, now);
            Assert.NotNull(prediction);
            minutesOut.Add((prediction.Arrival - now).TotalMinutes);
        }

        // Each poll leaves strictly less of the journey to run. A clock-sliding
        // estimate would hold roughly level instead.
        Assert.True(
            minutesOut[^1] < minutesOut[0] - 3.0,
            $"estimate went from {minutesOut[0]:F1} to {minutesOut[^1]:F1} minutes out");
    }

    /// <summary>
    /// §5 and the guard shared by all three bases. On 17 Sep the bus reached the
    /// stop at 08:04 and the estimate went on predicting the morning arrival for
    /// five more minutes, drifting later each poll: 08:05:15, 08:07:44, 08:09:46.
    /// </summary>
    [Fact]
    public void OnceTheBusHasArrived_TodaysRunIsNoLongerTheNextOne()
    {
        PredictionEngine engine = WithTwoPastMornings();
        DateTimeOffset arrival = EngineFixtures.LocalAt(2026, 9, 17, 8, 4);

        // Inside the arrival threshold, inside the run window.
        engine.Observe(
            EngineFixtures.MorningOnly,
            EngineFixtures.Reading(EngineFixtures.WhereItWasWith(0), 0.1),
            arrival);

        ArrivalPrediction? prediction = engine.PredictNextArrival(
            EngineFixtures.MorningOnly, arrival.AddMinutes(1));

        Assert.NotNull(prediction);
        Assert.NotEqual(EngineFixtures.Clock.DateOf(arrival), EngineFixtures.Clock.DateOf(prediction.Arrival));
    }

    /// <summary>
    /// §3.7. The route match is accurate AND noisy: it re-answers from wherever
    /// the bus is now, and that moves by whole minutes between polls. Publishing
    /// every wobble drove 25 notification pushes in 20 minutes.
    /// </summary>
    [Fact]
    public void PublishedArrival_IsHeldWhileItStaysInsideTheBand()
    {
        PredictionEngine engine = WithTwoPastMornings();
        DateTimeOffset first = EngineFixtures.LocalAt(2026, 9, 17, 7, 50);

        engine.Observe(
            EngineFixtures.MorningOnly, EngineFixtures.Reading(EngineFixtures.WhereItWasWith(600), 2.0), first);
        ArrivalPrediction? published = engine.PredictNextArrival(EngineFixtures.MorningOnly, first);
        Assert.NotNull(published);

        // Half a minute later, a position implying fifteen seconds more to run.
        // That is noise, not news.
        DateTimeOffset next = first.AddSeconds(30);
        engine.Observe(
            EngineFixtures.MorningOnly, EngineFixtures.Reading(EngineFixtures.WhereItWasWith(585), 1.9), next);
        ArrivalPrediction? held = engine.PredictNextArrival(EngineFixtures.MorningOnly, next);

        Assert.NotNull(held);
        Assert.Equal(published.Arrival, held.Arrival);
    }

    /// <summary>
    /// §3.7, the other half. A genuinely different bus still moves the answer —
    /// the hold must not become a freeze.
    /// </summary>
    [Fact]
    public void PublishedArrival_MovesWhenTheEstimateLeavesTheBand()
    {
        PredictionEngine engine = WithTwoPastMornings();
        DateTimeOffset first = EngineFixtures.LocalAt(2026, 9, 17, 7, 45);

        engine.Observe(
            EngineFixtures.MorningOnly, EngineFixtures.Reading(EngineFixtures.WhereItWasWith(900), 3.0), first);
        ArrivalPrediction? published = engine.PredictNextArrival(EngineFixtures.MorningOnly, first);
        Assert.NotNull(published);

        // A poll later the bus is a long way further along than the clock
        // accounts for: it skipped a stop and is genuinely early.
        DateTimeOffset next = first.AddSeconds(30);
        engine.Observe(
            EngineFixtures.MorningOnly, EngineFixtures.Reading(EngineFixtures.WhereItWasWith(300), 1.0), next);
        ArrivalPrediction? moved = engine.PredictNextArrival(EngineFixtures.MorningOnly, next);

        Assert.NotNull(moved);
        Assert.NotEqual(published.Arrival, moved.Arrival);
        Assert.True(moved.Arrival < published.Arrival, "an early bus must be reported early");
    }

    /// <summary>
    /// §3.6. The band is never narrower than the floor. On 17 Sep a two-sample
    /// match reported earliest and latest as the same instant — uncertainty of
    /// zero, from a sample of two.
    /// </summary>
    [Fact]
    public void RouteBand_IsNeverNarrowerThanItsFloor()
    {
        PredictionEngine engine = WithTwoPastMornings();
        DateTimeOffset now = EngineFixtures.LocalAt(2026, 9, 17, 7, 50);

        engine.Observe(
            EngineFixtures.MorningOnly, EngineFixtures.Reading(EngineFixtures.WhereItWasWith(600), 2.0), now);
        ArrivalPrediction? prediction = engine.PredictNextArrival(EngineFixtures.MorningOnly, now);

        Assert.NotNull(prediction);
        Assert.NotNull(prediction.Uncertainty);

        // A LITERAL, not Tuning.RouteBandFloor. Asserting against the constant
        // under test makes the assertion move with it, so the test passes
        // however small the floor becomes -- including zero.
        Assert.True(
            prediction.Uncertainty.Value >= TimeSpan.FromSeconds(30),
            $"band was {prediction.Uncertainty.Value.TotalSeconds}s wide");
    }

    /// <summary>
    /// §3.5. A Friday evening has to reach Monday. The search used to look one
    /// day ahead and nothing knew about weekends, so it read "tomorrow 08:01".
    /// </summary>
    [Fact]
    public void FridayEvening_PredictsMonday()
    {
        PredictionEngine engine = WithTwoPastMornings();

        // 18 Sep 2026 is a Friday.
        DateTimeOffset fridayEvening = EngineFixtures.LocalAt(2026, 9, 18, 19, 0);
        ArrivalPrediction? prediction = engine.PredictNextArrival(EngineFixtures.MorningOnly, fridayEvening);

        Assert.NotNull(prediction);
        Assert.Equal(DayOfWeek.Monday, EngineFixtures.Clock.ToLocal(prediction.Arrival).DayOfWeek);
    }

    /// <summary>
    /// §3.5. With nothing learned, the timetable is all there is — and it is
    /// honestly labelled as such, because a window centred on a timetable twenty
    /// minutes out is the failure that hides behind it.
    /// </summary>
    [Fact]
    public void WithNoHistory_FallsBackToTheTimetable_AndSaysSo()
    {
        PredictionEngine engine = new(EngineFixtures.Clock);
        DateTimeOffset now = EngineFixtures.LocalAt(2026, 9, 17, 6, 0);

        ArrivalPrediction? prediction = engine.PredictNextArrival(EngineFixtures.MorningOnly, now);

        Assert.NotNull(prediction);
        Assert.Equal(PredictionSource.Scheduled, prediction.Source);
        Assert.Equal(PredictionBasis.Scheduled, prediction.Basis);
        Assert.Equal(new TimeOnly(8, 0), TimeOnly.FromDateTime(EngineFixtures.Clock.ToLocal(prediction.Arrival)));
    }

    /// <summary>
    /// §3.5. The learned centre beats the timetable. These arrivals landed at
    /// 08:00 and 08:02 against an 08:00 timetable, so the learned median leads.
    /// </summary>
    [Fact]
    public void LearnedTime_IsTheMedianOfWhatActuallyHappened()
    {
        PredictionEngine engine = WithTwoPastMornings();

        (TimeOnly? learned, int samples, int? spread, int outliers) =
            engine.LearnedTime(EngineFixtures.ChildId, Run.Am);

        Assert.Equal(new TimeOnly(8, 1), learned);
        Assert.Equal(2, samples);
        Assert.Equal(2, spread);
        Assert.Equal(0, outliers);
    }

    /// <summary>
    /// §3.5. A run does not stop being the next arrival because its predicted
    /// time went by. The bus is late, not cancelled.
    /// </summary>
    [Fact]
    public void ALateBus_IsStillTodaysNextArrival()
    {
        PredictionEngine engine = WithTwoPastMornings();

        // Ten minutes past the learned 08:01, with the window open until 08:31.
        DateTimeOffset late = EngineFixtures.LocalAt(2026, 9, 17, 8, 11);
        ArrivalPrediction? prediction = engine.PredictNextArrival(EngineFixtures.MorningOnly, late);

        Assert.NotNull(prediction);
        Assert.Equal(EngineFixtures.Clock.DateOf(late), EngineFixtures.Clock.DateOf(prediction.Arrival));
    }
}
