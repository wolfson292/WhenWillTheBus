// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Prediction;
using WhenWillTheBus.Core.Storage;

namespace WhenWillTheBus.Core.Tests;

public sealed class HistoryExportTests
{
    /// <summary>
    /// The export exists so the phone can take history from the worker, so what
    /// matters is that the importer reads back exactly what was written. A
    /// format that only nearly round-trips would lose route tracks silently, and
    /// the only symptom would be estimates that never improve.
    /// </summary>
    [Fact]
    public void RoundTrips_ThroughTheImporter()
    {
        RunArrival morning = EngineFixtures.PastMorning(EngineFixtures.LocalAt(2026, 9, 15, 8, 0)) with
        {
            Recedes = 2,
            Stale = 3,
            Boarded = EngineFixtures.LocalAt(2026, 9, 15, 7, 56),
        };
        RunArrival afternoon = new()
        {
            Run = Run.Pm,
            Arrival = EngineFixtures.LocalAt(2026, 9, 15, 17, 21),
            ClosestMiles = 0.08,
            Substitute = true,
            Legs = new Dictionary<int, int> { [1] = 330, [3] = 90 },
        };

        string bundle = HistoryExport.ToBundle([(4242L, new[] { morning, afternoon })]);
        ImportedRider back = Assert.Single(HistoryImport.Parse(bundle));

        Assert.Equal("4242", back.ChildId);
        Assert.Equal(2, back.Arrivals.Count);

        RunArrival restoredMorning = back.Arrivals.Single(a => a.Run == Run.Am);
        Assert.Equal(morning.Arrival, restoredMorning.Arrival);
        Assert.Equal(morning.Track.Count, restoredMorning.Track.Count);
        Assert.Equal(morning.Legs[0], restoredMorning.Legs[0]);
        Assert.Equal(2, restoredMorning.Recedes);
        Assert.Equal(3, restoredMorning.Stale);
        Assert.Equal(morning.Boarded, restoredMorning.Boarded);

        RunArrival restoredAfternoon = back.Arrivals.Single(a => a.Run == Run.Pm);
        Assert.True(restoredAfternoon.Substitute);
        Assert.Equal(330, restoredAfternoon.Legs[1]);
        Assert.Equal(90, restoredAfternoon.Legs[3]);
    }

    /// <summary>
    /// The track is the field the route matcher depends on, and the one most
    /// easily lost to a rounding or ordering slip.
    /// </summary>
    [Fact]
    public void KeepsTrackPointsOldestFirst_AndAtMetrePrecision()
    {
        RunArrival arrival = new()
        {
            Run = Run.Am,
            Arrival = EngineFixtures.LocalAt(2026, 9, 15, 8, 0),
            ClosestMiles = 0.05,
            Track = [new TrackPoint(600, 40.7350123456, -74.0200987654), new TrackPoint(570, 40.736, -74.021)],
        };

        RunArrival back = HistoryImport.Parse(HistoryExport.ToBundle([(1L, new[] { arrival })]))[0].Arrivals[0];

        Assert.Equal(600, back.Track[0].SecondsBeforeArrival);
        Assert.Equal(570, back.Track[1].SecondsBeforeArrival);
        Assert.Equal(40.73501, back.Track[0].Latitude, precision: 9);
    }

    /// <summary>A rider with nothing learned yet is still a valid bundle.</summary>
    [Fact]
    public void EmptyHistory_IsStillReadable() =>
        Assert.Empty(HistoryImport.Parse(HistoryExport.ToBundle([(7L, Array.Empty<RunArrival>())]))[0].Arrivals);
}
