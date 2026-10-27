// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Prediction;
using WhenWillTheBus.Core.Storage;

namespace WhenWillTheBus.Core.Tests;

/// <summary>
/// What the worker learns about a run afterwards -- its road and its review --
/// has to survive every place history goes: the file, the bundle the phone
/// takes, and the merge when the phone already has the run.
/// </summary>
public sealed class CleanupFieldsTests
{
    private static readonly LocalClock Clock = new(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));

    private static RunArrival Cleaned() => new()
    {
        Run = Run.Am,
        Arrival = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero),
        ClosestMiles = 0.1,
        Track = [new TrackPoint(120, 26.45405, -81.82277), new TrackPoint(60, 26.45284, -81.82350)],
        Road = [new GeoPoint(26.45405, -81.82277), new GeoPoint(26.45290, -81.82181), new GeoPoint(26.45284, -81.82350)],
        RoadConfidence = 0.98,
        Review = new RunReview(RunVerdict.Detour, "Took Pine Rd from the east end."),
    };

    [Fact]
    public async Task TheRoadAndReviewSurviveTheHistoryFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"wwtb-cleanup-{Guid.NewGuid():N}.json");
        try
        {
            await HistoryStore.SaveAsync(path, [new StoredRider { ChildId = 7, Arrivals = [Cleaned()] }]);
            RunArrival back = Assert.Single(Assert.Single(await HistoryStore.LoadAsync(path)).Arrivals);

            Assert.Equal(3, back.Road.Count);
            Assert.Equal(0.98, back.RoadConfidence);
            Assert.Equal(new RunReview(RunVerdict.Detour, "Took Pine Rd from the east end."), back.Review);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheRoadAndReviewReachThePhone()
    {
        string bundle = HistoryExport.ToBundle([(7, [Cleaned()])]);
        RunArrival back = Assert.Single(Assert.Single(HistoryImport.Parse(bundle)).Arrivals);

        Assert.Equal(3, back.Road.Count);
        Assert.Equal(RunVerdict.Detour, back.Review?.Verdict);
    }

    /// <summary>A run never matched reads as not yet tried, so the worker tries it.</summary>
    [Fact]
    public void AnUncleanedRunReadsAsNotYetTried()
    {
        RunArrival plain = Cleaned() with { Road = [], RoadConfidence = null, Review = null };
        RunArrival back = Assert.Single(Assert.Single(HistoryImport.Parse(HistoryExport.ToBundle([(7, [plain])]))).Arrivals);

        Assert.Null(back.RoadConfidence);
        Assert.Empty(back.Road);
        Assert.Null(back.Review);
    }

    /// <summary>
    /// The phone's own record of a run it watched can know MORE legs than the
    /// worker's and so win the merge -- but it never has a road or review,
    /// which only the worker writes. Winning must not throw those away.
    /// </summary>
    [Fact]
    public void AMergeKeepsTheRoadWhicheverRecordWins()
    {
        PredictionEngine engine = new(Clock);
        RunArrival phone = Cleaned() with
        {
            Road = [],
            RoadConfidence = null,
            Review = null,
            Legs = new Dictionary<int, int> { [0] = 600, [1] = 400, [2] = 300, [3] = 120 },
        };

        engine.LoadHistory(7, [phone]);
        engine.LoadHistory(7, [Cleaned()]);

        RunArrival merged = Assert.Single(engine.ArrivalsFor(7));
        Assert.Equal(4, merged.Legs.Count);
        Assert.Equal(3, merged.Road.Count);
        Assert.Equal(RunVerdict.Detour, merged.Review?.Verdict);
    }

    [Fact]
    public void AmendReplacesJustThatRun()
    {
        PredictionEngine engine = new(Clock);
        RunArrival other = Cleaned() with { Run = Run.Pm, Arrival = new DateTimeOffset(2026, 10, 26, 21, 20, 0, TimeSpan.Zero), Review = null };
        engine.LoadHistory(7, [Cleaned() with { Review = null }, other]);

        IReadOnlyList<RunArrival> before = engine.ArrivalsFor(7);
        Assert.True(engine.Amend(7, Run.Am, Cleaned().Arrival, item => item with { Review = new RunReview(RunVerdict.Usual, "As usual.") }));

        Assert.Equal(RunVerdict.Usual, engine.ArrivalsFor(7).Single(item => item.Run == Run.Am).Review?.Verdict);
        Assert.Null(engine.ArrivalsFor(7).Single(item => item.Run == Run.Pm).Review);

        // A new list: a reader still holding the old one is not disturbed.
        Assert.Null(before.Single(item => item.Run == Run.Am).Review);
        Assert.False(engine.Amend(7, Run.Am, Cleaned().Arrival.AddDays(1), item => item));
    }
}
