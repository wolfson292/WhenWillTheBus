// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core.Api;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Prediction;
using WhenWillTheBus.Core.Storage;

namespace WhenWillTheBus.Core.Tests;

public sealed class HistoryStoreTests
{
    private static StoredRider Sample() => new()
    {
        ChildId = 9911,
        Arrivals =
        [
            new RunArrival
            {
                Run = Run.Am,
                Arrival = new DateTimeOffset(2026, 9, 17, 12, 4, 34, TimeSpan.Zero),
                ClosestMiles = 0.04,
                Legs = new Dictionary<int, int> { [0] = 570, [3] = 95 },
                Track = [new TrackPoint(1200, 40.73501, -74.02003), new TrackPoint(1170, 40.73381, -74.01913)],
                Recedes = 1,
                Stale = 2,
                Boarded = new DateTimeOffset(2026, 9, 17, 11, 56, 0, TimeSpan.Zero),
            },
            new RunArrival
            {
                Run = Run.Pm,
                Arrival = new DateTimeOffset(2026, 9, 17, 21, 20, 0, TimeSpan.Zero),
                ClosestMiles = 0.08,
                Substitute = true,
                Replayed = true,
            },
        ],
        Scans =
        [
            new ScanEvent(new DateTimeOffset(2026, 9, 17, 11, 56, 0, TimeSpan.Zero), "Maple St", ScanKind.Pickup, "badge"),
            new ScanEvent(new DateTimeOffset(2026, 9, 17, 12, 30, 0, TimeSpan.Zero), "Example Elementary", ScanKind.Dropoff, null),
        ],
    };

    [Fact]
    public void RoundTrip_KeepsEverythingTheEstimateLearnsFrom()
    {
        StoredRider original = Sample();

        StoredRider restored = Assert.Single(HistoryStore.Deserialise(HistoryStore.Serialise([original])));

        Assert.Equal(original.ChildId, restored.ChildId);
        Assert.Equal(2, restored.Arrivals.Count);

        RunArrival morning = restored.Arrivals[0];
        Assert.Equal(Run.Am, morning.Run);
        Assert.Equal(original.Arrivals[0].Arrival, morning.Arrival);
        Assert.Equal(570, morning.Legs[0]);
        Assert.Equal(95, morning.Legs[3]);
        Assert.Equal(2, morning.Track.Count);
        Assert.Equal(1200, morning.Track[0].SecondsBeforeArrival);
        Assert.Equal(1, morning.Recedes);
        Assert.Equal(2, morning.Stale);
        Assert.Equal(original.Arrivals[0].Boarded, morning.Boarded);

        RunArrival afternoon = restored.Arrivals[1];
        Assert.True(afternoon.Substitute);
        Assert.True(afternoon.Replayed);

        Assert.Equal(2, restored.Scans.Count);
        Assert.Equal(ScanKind.Pickup, restored.Scans[0].Kind);
        Assert.Equal("Example Elementary", restored.Scans[1].Location);
    }

    /// <summary>§4. Five decimal places, a little over a metre.</summary>
    [Fact]
    public void Serialise_RoundsCoordinatesRatherThanRecordingGpsNoise()
    {
        StoredRider rider = Sample() with
        {
            Arrivals =
            [
                new RunArrival
                {
                    Run = Run.Am,
                    Arrival = DateTimeOffset.UnixEpoch,
                    ClosestMiles = 0.1,
                    Track = [new TrackPoint(30, 40.7350123456, -74.0200987654)],
                },
            ],
        };

        string json = HistoryStore.Serialise([rider]);

        Assert.Contains("40.73501", json, StringComparison.Ordinal);
        Assert.DoesNotContain("40.7350123", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialise_TreatsNothingAsNothing()
    {
        Assert.Empty(HistoryStore.Deserialise(""));
        Assert.Empty(HistoryStore.Deserialise("   "));
    }

    /// <summary>
    /// A store written by a newer build is not readable, and guessing at it would
    /// quietly learn from a shape that means something else.
    /// </summary>
    [Fact]
    public void Deserialise_RefusesAFutureVersion() =>
        Assert.Throws<InvalidDataException>(() =>
            HistoryStore.Deserialise($$"""{"version": {{HistoryStore.Version + 1}}, "riders": []}"""));

    /// <summary>
    /// A torn write would cost six weeks of learned journeys, so the file is
    /// written beside the target and moved into place.
    /// </summary>
    [Fact]
    public async Task Save_ReplacesAtomically_AndLeavesNoTemporaryBehind()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"wwtb-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "history.json");

        try
        {
            await HistoryStore.SaveAsync(path, [Sample()]);
            await HistoryStore.SaveAsync(path, [Sample() with { ChildId = 5 }]);

            Assert.False(File.Exists(path + ".tmp"));
            Assert.Equal(5, Assert.Single(await HistoryStore.LoadAsync(path)).ChildId);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Load_OfSomethingThatWasNeverSaved_IsEmpty() =>
        Assert.Empty(await HistoryStore.LoadAsync(Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.json")));

    /// <summary>
    /// The learned history survives a save and reload well enough to keep
    /// predicting — the point of storing it at all.
    /// </summary>
    [Fact]
    public void RestoredHistory_StillCarriesARouteEstimate()
    {
        PredictionEngine engine = new(EngineFixtures.Clock);
        engine.LoadHistory(
            EngineFixtures.ChildId,
            [
                EngineFixtures.PastMorning(EngineFixtures.LocalAt(2026, 9, 15, 8, 0)),
                EngineFixtures.PastMorning(EngineFixtures.LocalAt(2026, 9, 16, 8, 2)),
            ]);

        string json = HistoryStore.Serialise(
            [new StoredRider { ChildId = EngineFixtures.ChildId, Arrivals = engine.ArrivalsFor(EngineFixtures.ChildId) }]);

        PredictionEngine restored = new(EngineFixtures.Clock);
        restored.LoadHistory(EngineFixtures.ChildId, HistoryStore.Deserialise(json)[0].Arrivals);

        DateTimeOffset now = EngineFixtures.LocalAt(2026, 9, 17, 7, 50);
        restored.Observe(
            EngineFixtures.MorningOnly, EngineFixtures.Reading(EngineFixtures.WhereItWasWith(600), 2.0), now);

        ArrivalPrediction? prediction = restored.PredictNextArrival(EngineFixtures.MorningOnly, now);

        Assert.NotNull(prediction);
        Assert.Equal(PredictionBasis.Route, prediction.Basis);
    }
}
