// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Prediction;

namespace WhenWillTheBus.Core.Tests;

/// <summary>
/// The stage machine, exercised at instants of a school day.
/// </summary>
/// <remarks>
/// It had NO tests of its own, and the fault that prompted these is exactly
/// what that allowed: the morning ride to school could not be shown at all
/// until a morning drop-off had already been recorded, and no fixture ever
/// asked what happened on the days before one had.
/// </remarks>
public sealed class JourneyStageMachineTests
{
    private static readonly LocalClock Clock = EngineFixtures.Clock;

    private static DateTimeOffset At(int hour, int minute, int day = 22) =>
        EngineFixtures.LocalAt(2026, 9, day, hour, minute);

    /// <summary>
    /// The real morning of 22 Sep: boarded at 08:01, and the only prediction in
    /// hand is the afternoon pickup at 17:21.
    /// </summary>
    private static StageInputs Morning(DateTimeOffset now, DateTimeOffset? school = null) => new()
    {
        Now = now,
        DistanceMiles = 4.2,
        NextArrival = At(17, 21),
        NextRun = Run.Pm,
        PredictionSource = PredictionSource.Learned,
        SchoolArrival = school,
        LastPickup = At(8, 1),
        ApproachOpen = false,
    };

    /// <summary>
    /// THE REPORTED FAULT. With no drop-off ever recorded there is no school
    /// arrival to learn from, and the stage used to require one — so the rider
    /// boarded, the ride vanished, and the page counted down to the AFTERNOON
    /// pickup for the whole journey to school.
    /// </summary>
    [Fact]
    public void RidingToSchool_IsShownEvenWithNothingLearnedAboutWhenItEnds()
    {
        Journey journey = JourneyStageMachine.Evaluate(Morning(At(8, 30)), Clock);

        Assert.Equal(JourneyStage.ToSchool, journey.Stage);

        // Null, not the afternoon pickup, and not zero: nothing is known about
        // when this ride ends, and a bar at 0% would claim it had not started.
        Assert.Null(journey.Target);
        Assert.Null(journey.Progress);
        Assert.Equal(At(8, 1), journey.Boarded);
    }

    [Fact]
    public void RidingToSchool_FillsTowardsALearnedArrival()
    {
        Journey journey = JourneyStageMachine.Evaluate(Morning(At(8, 43), school: At(9, 25)), Clock);

        Assert.Equal(JourneyStage.ToSchool, journey.Stage);
        Assert.Equal(At(9, 25), journey.Target);

        // 42 minutes into an 84-minute ride.
        Assert.Equal(50, journey.Progress);
    }

    /// <summary>
    /// A late bus pins the bar; it does not end the journey. The school arrival
    /// used to roll to TOMORROW the moment it passed, which put the target on
    /// the wrong day and collapsed the stage to idle mid-ride.
    /// </summary>
    [Fact]
    public void ARideRunningLateStillCountsAsARide()
    {
        Journey journey = JourneyStageMachine.Evaluate(Morning(At(9, 40), school: At(9, 25)), Clock);

        Assert.Equal(JourneyStage.ToSchool, journey.Stage);
        Assert.Equal(100, journey.Progress);
    }

    /// <summary>The drop-off is what ends the ride, and it is the school that records it.</summary>
    [Fact]
    public void TheDropoffEndsTheRide()
    {
        StageInputs input = Morning(At(9, 27)) with { LastDropoff = At(9, 25) };

        Assert.Equal(JourneyStage.AtSchool, JourneyStageMachine.Evaluate(input, Clock).Stage);
    }

    [Fact]
    public void OnceTheDwellIsOverTheDayGoesQuiet()
    {
        StageInputs input = Morning(At(9, 45)) with { LastDropoff = At(9, 25) };

        Assert.Equal(JourneyStage.Idle, JourneyStageMachine.Evaluate(input, Clock).Stage);
    }

    /// <summary>
    /// A drop-off scan that never comes must not leave a ride running all day.
    /// Bounding it is the whole reason the stage may go without a target.
    /// </summary>
    [Fact]
    public void ARideWithNothingLearnedIsStillBounded()
    {
        Assert.Equal(
            JourneyStage.ToSchool,
            JourneyStageMachine.Evaluate(Morning(At(9, 55)), Clock).Stage);

        Assert.Equal(
            JourneyStage.Idle,
            JourneyStageMachine.Evaluate(Morning(At(10, 5)), Clock).Stage);
    }

    /// <summary>With a target in hand the bound is tighter: the target plus its overrun.</summary>
    [Fact]
    public void ALearnedRideIsBoundedByItsOwnTarget()
    {
        Assert.Equal(
            JourneyStage.ToSchool,
            JourneyStageMachine.Evaluate(Morning(At(10, 5), school: At(9, 25)), Clock).Stage);

        Assert.Equal(
            JourneyStage.Idle,
            JourneyStageMachine.Evaluate(Morning(At(10, 15), school: At(9, 25)), Clock).Stage);
    }

    /// <summary>
    /// Being aboard outranks the bus being nearby. Rule 4 never checks for a
    /// boarding scan, so a morning stage that fell through to it described a
    /// rider already on the bus as still waiting at the kerb.
    /// </summary>
    [Fact]
    public void BeingAboardOutranksTheBusApproachingTheStop()
    {
        StageInputs input = Morning(At(8, 30)) with
        {
            ApproachOpen = true,
            NextArrival = At(8, 40),
            NextRun = Run.Am,
        };

        Assert.Equal(JourneyStage.ToSchool, JourneyStageMachine.Evaluate(input, Clock).Stage);
    }

    /// <summary>
    /// The afternoon counts towards the HOME STOP, and that prediction rolls to
    /// the next school day once the run is over — so it is bounded the same way.
    /// </summary>
    [Fact]
    public void TheAfternoonRideHomeCountsTowardsTheStop()
    {
        StageInputs input = new()
        {
            Now = At(16, 30),
            NextArrival = At(17, 21),
            NextRun = Run.Pm,
            PredictionSource = PredictionSource.Learned,
            LastPickup = At(15, 51),
        };

        Journey journey = JourneyStageMachine.Evaluate(input, Clock);

        Assert.Equal(JourneyStage.FromSchool, journey.Stage);
        Assert.Equal(At(17, 21), journey.Target);
    }

    /// <summary>
    /// A target on a different day is DROPPED, not treated as disqualifying.
    /// Being aboard does not stop being true because we cannot say when it ends.
    /// </summary>
    [Fact]
    public void ATargetOnAnotherDayIsIgnoredRatherThanEndingTheJourney()
    {
        StageInputs input = new()
        {
            Now = At(16, 30),
            NextArrival = EngineFixtures.LocalAt(2026, 9, 23, 8, 1),
            NextRun = Run.Am,
            PredictionSource = PredictionSource.Learned,
            LastPickup = At(15, 51),
        };

        Journey journey = JourneyStageMachine.Evaluate(input, Clock);

        Assert.Equal(JourneyStage.FromSchool, journey.Stage);
        Assert.Null(journey.Target);
    }
}
