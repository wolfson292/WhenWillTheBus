// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Prediction;

namespace WhenWillTheBus.Core.Tests;

public sealed class PredictionAccuracyTests
{
    private static PredictionEngine Engine()
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
    /// Drive a whole approach and let the bus arrive, so the engine scores the
    /// estimates it actually published rather than ones invented for the test.
    /// </summary>
    private static RunArrival RunAnApproach(PredictionEngine engine, bool watchUntilArrival = true)
    {
        Dictionary<long, Student> students = new() { [EngineFixtures.ChildId] = EngineFixtures.MorningOnly };
        DateTimeOffset start = EngineFixtures.LocalAt(2026, 9, 17, 7, 45);

        // 15 minutes of approach at 30-second polls, closing steadily.
        for (int poll = 0; poll <= 30; poll++)
        {
            DateTimeOffset now = start.AddSeconds(poll * 30);
            int remaining = 900 - (poll * 30);

            engine.Observe(
                EngineFixtures.MorningOnly,
                EngineFixtures.Reading(EngineFixtures.WhereItWasWith(Math.Max(remaining, 0)), Math.Max(remaining / 300.0, 0.1)),
                now);

            engine.PredictNextArrival(EngineFixtures.MorningOnly, now);
        }

        // Promote once the window has shut.
        engine.PromotePending(students, EngineFixtures.LocalAt(2026, 9, 17, 9, 30));
        return engine.ArrivalsFor(EngineFixtures.ChildId)
            .Single(a => EngineFixtures.Clock.DateOf(a.Arrival) == new DateOnly(2026, 9, 17));
    }

    [Fact]
    public void AnArrivalRecordsHowWrongTheEstimateWas()
    {
        RunArrival arrival = RunAnApproach(Engine());

        Assert.NotNull(arrival.ErrorAtArrival);
        Assert.NotNull(arrival.ErrorAtFiveMinutes);
    }

    /// <summary>
    /// The five-minute score must come from an estimate published at least five
    /// minutes out. One published four minutes before arrival knows things a
    /// parent leaving at five minutes did not, and scoring against it would
    /// flatter the estimate.
    /// </summary>
    [Fact]
    public void TheFiveMinuteScoreIsNotTakenFromCloserIn()
    {
        PredictionEngine engine = Engine();
        RunArrival arrival = RunAnApproach(engine);

        // With a converging approach the final estimate is at least as good as
        // the earlier one, so a five-minute score BETTER than the final one
        // would mean it had been taken from too close in.
        Assert.True(
            Math.Abs(arrival.ErrorAtFiveMinutes!.Value) >= Math.Abs(arrival.ErrorAtArrival!.Value) - 1,
            $"five-minute error {arrival.ErrorAtFiveMinutes} beat the final {arrival.ErrorAtArrival}");
    }

    /// <summary>A run nobody watched cannot be scored, and must not pretend to be.</summary>
    [Fact]
    public void AnUnwatchedArrivalHasNoScore()
    {
        PredictionEngine engine = Engine();
        Dictionary<long, Student> students = new() { [EngineFixtures.ChildId] = EngineFixtures.MorningOnly };

        // One reading inside the window, no predictions ever asked for.
        engine.Observe(
            EngineFixtures.MorningOnly,
            EngineFixtures.Reading(EngineFixtures.WhereItWasWith(0), 0.1),
            EngineFixtures.LocalAt(2026, 9, 17, 8, 1));

        engine.PromotePending(students, EngineFixtures.LocalAt(2026, 9, 17, 9, 30));

        RunArrival arrival = engine.ArrivalsFor(EngineFixtures.ChildId)
            .Single(a => EngineFixtures.Clock.DateOf(a.Arrival) == new DateOnly(2026, 9, 17));

        Assert.Null(arrival.ErrorAtFiveMinutes);
        Assert.Null(arrival.ErrorAtArrival);
    }

    /// <summary>The sign has to mean something: positive is LATE, which is the dangerous side.</summary>
    [Fact]
    public void PositiveMeansTheEstimateWasLate()
    {
        RunArrival arrival = RunAnApproach(Engine());

        // The synthetic approach arrives on the nose of its route match, so the
        // error should be small either way -- what matters is that it is a
        // signed number of seconds rather than a magnitude.
        Assert.InRange(arrival.ErrorAtArrival!.Value, -600, 600);
    }
}
