// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core.Api;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Prediction;

namespace WhenWillTheBus.Core.Tests;

/// <summary>
/// The morning ride outlives the window that watches for the bus.
/// </summary>
/// <remarks>
/// TWO DIFFERENT QUESTIONS, answered for months by one predicate. The approach
/// window is built around the arrival at the RIDER'S STOP and closes thirty
/// minutes after it; the morning journey BEGINS at that arrival and runs for
/// another hour to the school.
///
/// On 29 Sep 2026 the pickup was at 07:59 and the window closed at 08:31. The
/// worker polled and published only while watching, so the Lock Screen card
/// stopped being updated 36% of the way to school and sat frozen there —
/// never advanced, never even cleared — while the app, which evaluates the
/// stage on every request, showed the same ride correctly all the way to the
/// end. Anything asking "is there something to show" must ask the STAGE.
/// </remarks>
public sealed class RideBeyondTheWindowTests
{
    private static readonly TimeOnly Scheduled = new(7, 56);

    private static ScanEvent Scan(int day, int hour, int minute, int second, ScanKind kind) =>
        new(EngineFixtures.LocalAt(2026, 9, day, hour, minute).AddSeconds(second), "somewhere", kind, null);

    /// <summary>The 29 Sep history exactly as the worker held it.</summary>
    private static Student Rider() => EngineFixtures.MorningOnly with
    {
        AmScheduled = Scheduled,
        Scans =
        [
            Scan(28, 8, 1, 30, ScanKind.Pickup),
            Scan(28, 9, 26, 6, ScanKind.Dropoff),
            Scan(29, 7, 59, 42, ScanKind.Pickup),
        ],
    };

    private static Journey StageAt(int hour, int minute)
    {
        Student rider = Rider();
        DateTimeOffset now = EngineFixtures.LocalAt(2026, 9, 29, hour, minute);
        PredictionEngine engine = new(EngineFixtures.Clock);

        return engine.Stage(
            rider, now, null, SchoolArrivalPredictor.Predict(rider, now, EngineFixtures.Clock)?.Arrival);
    }

    private static bool WatchedAt(int hour, int minute)
    {
        Student rider = Rider();
        DateTimeOffset now = EngineFixtures.LocalAt(2026, 9, 29, hour, minute);
        PredictionEngine engine = new(EngineFixtures.Clock);

        return engine.IsWatching(rider, Run.Am, now) || engine.IsWatching(rider, Run.Pm, now);
    }

    /// <summary>
    /// The fact the whole fault rests on. If this ever fails the two questions
    /// have converged and the gate below stops mattering — but it will not, and
    /// the hour between them is the ride to school.
    /// </summary>
    [Fact]
    public void MidRideTheBusIsNoLongerWatched()
    {
        Assert.False(WatchedAt(9, 0));
        Assert.True(StageAt(9, 0).Active);
    }

    [Fact]
    public void TheRideToSchoolIsStillOnScreenLongAfterTheWindowShuts()
    {
        Journey journey = StageAt(9, 0);

        Assert.Equal(JourneyStage.ToSchool, journey.Stage);
        Assert.Equal(70, journey.Progress);
    }

    /// <summary>
    /// And it does END, rather than being left frozen: the overrun bounds it
    /// even on a day the school never records a drop-off, which 29 Sep was.
    /// </summary>
    [Fact]
    public void AndItEndsOnItsOwnWhenTheDropoffNeverComes()
    {
        Assert.True(StageAt(10, 0).Active);
        Assert.False(StageAt(10, 11).Active);
    }
}
