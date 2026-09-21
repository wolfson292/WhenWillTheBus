// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core.Model;

namespace WhenWillTheBus.Core.Prediction;

/// <summary>Everything the stage machine needs to answer, gathered in one place.</summary>
public sealed record StageInputs
{
    public required DateTimeOffset Now { get; init; }

    /// <summary>Distance from the bus to the rider's stop, in miles.</summary>
    public double? DistanceMiles { get; init; }

    public double ArrivalThresholdMiles { get; init; } = Tuning.ArrivalThresholdMiles;

    /// <summary>The outermost ladder rung, which the morning progress bar fills across.</summary>
    public double OuterRungMiles { get; init; } = Tuning.AnchorLadderMiles[0];

    public DateTimeOffset? NextArrival { get; init; }

    public Run? NextRun { get; init; }

    public PredictionSource? PredictionSource { get; init; }

    public DateTimeOffset? SchoolArrival { get; init; }

    public DateTimeOffset? LastPickup { get; init; }

    public DateTimeOffset? LastDropoff { get; init; }

    public bool ApproachOpen { get; init; }

    /// <summary>When this afternoon's ride home finished, if it has.</summary>
    public DateTimeOffset? RideHomeEnded { get; init; }
}

/// <summary>
/// Work out which stage of the school run a rider is currently in.
/// </summary>
/// <remarks>
/// This exists because the same question — "is a journey happening, and how far
/// through it" — was being answered independently in ten branches of an
/// automation, from whatever signals were nearest to hand. Four separate faults
/// came out of that, each a gap in one branch the other nine could not see: a
/// morning progress bar that filled for two hours after the rider was in class,
/// a bar lurching between 78% and 7%, a flickering title and colour, and a
/// countdown that ran 61 hours to the following Monday.
///
/// Deliberately PURE — no coordinator, no clock of its own — so every stage
/// transition can be tested directly at any instant of a school day.
/// </remarks>
public static class JourneyStageMachine
{
    /// <summary>The point a target rounds up to the next displayed minute.</summary>
    private const int HalfMinuteSeconds = 30;

    /// <summary>
    /// Return the rider's current stage.
    /// </summary>
    /// <remarks>
    /// ORDERING IS THE WHOLE DESIGN. Having arrived somewhere outranks being on
    /// the way there, and being aboard outranks the bus merely being nearby —
    /// otherwise an afternoon approach would describe a rider who is already on
    /// the bus as though they were still waiting for it.
    /// </remarks>
    public static Journey Evaluate(StageInputs input, LocalClock clock)
    {
        DateTimeOffset now = input.Now;
        bool atStop = input.DistanceMiles is not null
            && input.DistanceMiles.Value <= input.ArrivalThresholdMiles;

        // 0. THE RIDE HOME IS OVER, AND STAYS OVER.
        //
        //    A morning arrival STARTS a journey; an afternoon arrival ENDS one.
        //    Nothing encoded that asymmetry, so the afternoon could restart
        //    itself: on 16 Sep the bus reached the stop at 17:20, the rider got
        //    off, and the bus then worked the neighbourhood for another
        //    seventeen minutes — 0.0, 0.6, 1.3, 0.7, 0.4 — re-entering the rungs
        //    twice and announcing a ride home that had already finished.
        Journey? settled = AfterTheRideHome(input.RideHomeEnded, now, clock);
        if (settled is not null)
        {
            return settled;
        }

        // 1. Just scanned off the bus. Holds briefly, then lets the day go quiet.
        //
        //    WHERE they got off depends on which run it was. A drop-off scan in
        //    the morning is the school; in the afternoon it is the home stop.
        //    Reading every drop-off as the school announced "Arrived at school —
        //    dropped off safely" as the rider stepped off the bus outside the
        //    house.
        if (input.LastDropoff is not null && clock.SameDay(input.LastDropoff, now))
        {
            double since = (now - input.LastDropoff.Value).TotalSeconds;
            if (since >= 0 && since <= Tuning.ArrivedDwell.TotalSeconds)
            {
                bool atSchool = clock.RunOf(input.LastDropoff.Value) == Run.Am;
                return new Journey
                {
                    Stage = atSchool ? JourneyStage.AtSchool : JourneyStage.Home,
                    Progress = 100,
                    JourneyId = clock.JourneyId(input.LastDropoff.Value),
                };
            }
        }

        // 2. The bus is at the stop. In the afternoon that is the end of the ride
        //    home; in the morning it is the moment to walk out of the door.
        if (atStop && input.ApproachOpen && input.NextRun is not null)
        {
            // Which run this IS comes from the clock, not from which run is
            // predicted next. Those differ the moment the bus arrives, because
            // the prediction rolls straight on to the afternoon — and reading
            // the stage off it announced "Home" while the rider was still
            // standing at the kerb waiting to be let on for school.
            bool afternoon = clock.RunOf(now) == Run.Pm;
            return new Journey
            {
                Stage = afternoon ? JourneyStage.Home : JourneyStage.AtStop,
                Progress = 100,
                JourneyId = clock.JourneyId(now),
            };
        }

        // 3. Aboard. The bar fills against ELAPSED TIME, because distance to the
        //    home stop says nothing useful while the bus is working its route.
        if (input.LastPickup is not null && clock.SameDay(input.LastPickup, now))
        {
            DateTimeOffset boarded = input.LastPickup.Value;
            bool morning = clock.RunOf(boarded) == Run.Am;
            bool finished = input.LastDropoff is not null
                && clock.SameDay(input.LastDropoff, now)
                && input.LastDropoff.Value > boarded;

            DateTimeOffset? target = morning ? input.SchoolArrival : input.NextArrival;

            // The target has to be TODAY. Once the journey ends the predictions
            // roll to the next school day, and a bar filling towards a target
            // three days out is never right.
            if (!finished && target is not null && clock.SameDay(target, now))
            {
                return new Journey
                {
                    Stage = morning ? JourneyStage.ToSchool : JourneyStage.FromSchool,
                    Progress = Fraction(boarded, target.Value, now),
                    Target = ToTheMinute(target.Value),
                    Boarded = boarded,
                    JourneyId = clock.JourneyId(boarded),
                };
            }
        }

        // 4. Nobody aboard, but the bus is coming to collect them. MORNING ONLY,
        //    AND THAT ASYMMETRY IS THE POINT: in the morning there cannot be a
        //    scan yet, because the scan happens on boarding. In the afternoon
        //    there can be, and its absence means the rider is not on the bus.
        //
        //    An afternoon approach used to fire on the clock alone. On 15 Sep the
        //    rider did not ride, the bus ran a nearby route anyway, the window
        //    opened at 16:36 for a bus that was never coming — and the stage then
        //    flapped in and out of idle as the estimate slid past, starting and
        //    clearing three Live Activities in seventy minutes.
        //
        //    Only once the run has been LEARNED: anchored to a timetable twenty
        //    minutes out, this used to fire after the bus had already gone.
        if (input.ApproachOpen
            && clock.RunOf(now) == Run.Am
            && input.PredictionSource == Model.PredictionSource.Learned
            && input.NextArrival is not null
            && clock.SameDay(input.NextArrival, now)
            && input.NextRun is not null)
        {
            return new Journey
            {
                Stage = JourneyStage.ToStop,
                Progress = Closing(input.DistanceMiles, input.OuterRungMiles),
                Target = ToTheMinute(input.NextArrival.Value),
                JourneyId = clock.JourneyId(now),
            };
        }

        return Journey.Idle;
    }

    /// <summary>
    /// The settled stage once the afternoon ride has finished, or null when the
    /// ordinary rules still apply.
    /// </summary>
    /// <remarks>
    /// Holds the finished card for the dwell, then goes quiet. That the LAST
    /// thing pushed is a FINISHED card matters more than it looks: the
    /// notification no longer clears anything, so whatever it said last is what
    /// stands on the phone until iOS retires it. Ending mid-ride left "riding
    /// home, 7 min" frozen there.
    /// </remarks>
    private static Journey? AfterTheRideHome(DateTimeOffset? ended, DateTimeOffset now, LocalClock clock)
    {
        if (ended is null || !clock.SameDay(ended, now))
        {
            return null;
        }

        double since = (now - ended.Value).TotalSeconds;
        if (since < 0)
        {
            return null;
        }

        return since <= Tuning.ArrivedDwell.TotalSeconds
            ? new Journey
            {
                Stage = JourneyStage.Home,
                Progress = 100,
                JourneyId = clock.JourneyId(ended.Value),
            }
            : Journey.Idle;
    }

    /// <summary>How far <paramref name="now"/> is between two instants, as a percentage.</summary>
    private static int Fraction(DateTimeOffset start, DateTimeOffset end, DateTimeOffset now)
    {
        double span = Math.Max((end - start).TotalSeconds, 60.0);
        double done = (now - start).TotalSeconds / span;
        return Math.Clamp((int)Math.Round(done * 100, MidpointRounding.AwayFromZero), 0, 100);
    }

    /// <summary>How far the bus has closed towards the stop, as a percentage.</summary>
    private static int? Closing(double? distanceMiles, double outerMiles)
    {
        if (distanceMiles is null || outerMiles <= 0)
        {
            return null;
        }

        double closed = (outerMiles - Math.Min(distanceMiles.Value, outerMiles)) / outerMiles;
        return Math.Clamp((int)Math.Round(closed * 100, MidpointRounding.AwayFromZero), 0, 100);
    }

    /// <summary>
    /// Round a target to the minute it will be displayed as.
    /// </summary>
    /// <remarks>
    /// The estimate carries sub-second precision, so the raw target moves on
    /// every poll even when nothing has really changed. Anything watching it for
    /// a reason to act — a notification deciding whether to re-push — then fires
    /// every thirty seconds to say the same thing. A card shows minutes, so
    /// rounding here makes "has the estimate moved" a question the value can
    /// answer on its own.
    /// </remarks>
    public static DateTimeOffset ToTheMinute(DateTimeOffset moment)
    {
        DateTimeOffset floor = new(
            moment.Year, moment.Month, moment.Day, moment.Hour, moment.Minute, 0, moment.Offset);
        return moment.Second >= HalfMinuteSeconds ? floor.AddMinutes(1) : floor;
    }
}
