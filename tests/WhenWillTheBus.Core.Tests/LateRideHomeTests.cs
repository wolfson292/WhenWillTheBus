// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core.Api;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Prediction;

namespace WhenWillTheBus.Core.Tests;

/// <summary>
/// A late bus home is still watched, and still learned, while the rider is aboard.
/// </summary>
/// <remarks>
/// On 28 Oct a substitute ran the afternoon route half an hour late. The window
/// is centred on the learned time and shut with Willow aboard and the bus 2.6
/// miles out: the worker stopped watching, the Lock Screen card froze, and the
/// arrival was never recorded.
/// </remarks>
public sealed class LateRideHomeTests
{
    private static DateTimeOffset At(int hour, int minute) => EngineFixtures.LocalAt(2026, 10, 28, hour, minute);

    private static Student Rider(bool aboard) => EngineFixtures.MorningOnly with
    {
        PmScheduled = new TimeOnly(17, 20),
        Scans = aboard ? [new ScanEvent(At(16, 44), "school", ScanKind.Pickup, null)] : [],
    };

    [Fact]
    public void AboardTheBusHomeItIsWatchedPastTheUsualWindow()
    {
        PredictionEngine engine = new(EngineFixtures.Clock);

        Assert.True(engine.IsWatching(Rider(aboard: true), Run.Pm, At(18, 5)));
        Assert.False(engine.IsWatching(Rider(aboard: false), Run.Pm, At(18, 5)));
    }

    [Fact]
    public void NotForever()
    {
        PredictionEngine engine = new(EngineFixtures.Clock);

        Assert.False(engine.IsWatching(Rider(aboard: true), Run.Pm, At(19, 0)));
    }

    [Fact]
    public void TheLateArrivalIsLearned()
    {
        PredictionEngine engine = new(EngineFixtures.Clock);
        Student rider = Rider(aboard: true);
        Dictionary<long, Student> riders = new() { [rider.ChildId] = rider };

        engine.FoldReading(rider, At(17, 55), 2.6);
        engine.FoldReading(rider, At(18, 5), 0.1);

        // Still open while it is pending: the usual window shut at 5:50.
        Assert.True(engine.PromotePending(riders, At(18, 6)));

        RunArrival arrival = Assert.Single(engine.ArrivalsFor(rider.ChildId));
        Assert.Equal(Run.Pm, arrival.Run);
        Assert.Equal(At(18, 5), arrival.Arrival);
    }
}
