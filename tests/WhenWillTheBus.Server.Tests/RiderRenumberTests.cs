// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Prediction;
using WhenWillTheBus.Core.Storage;
using WhenWillTheBus.Server.Monitoring;

namespace WhenWillTheBus.Server.Tests;

/// <summary>
/// A rider whose id changes keeps their history.
/// </summary>
/// <remarks>
/// On 27 Oct WheresTheBus re-issued Willow's id overnight (18279837 became
/// 19457624). Everything was keyed by it, so the worker saw a new rider with no
/// past, the next save wrote the file without the old one, and both apps had
/// nothing to predict from that morning or afternoon.
/// </remarks>
public sealed class RiderRenumberTests
{
    private static readonly LocalClock Clock = new(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));

    private static Student Rider(long id, string name) => new() { ChildId = id, Name = name };

    private static RunArrival Arrival(int day, Run run) => new()
    {
        Run = run,
        ClosestMiles = 0.01,
        Arrival = new DateTimeOffset(2026, 10, day, run == Run.Am ? 12 : 21, 20, 0, TimeSpan.Zero),
    };

    [Fact]
    public void ARiderWithANewIdIsMatchedByName()
    {
        Dictionary<long, Student> roster = new() { [19457624] = Rider(19457624, "Willow Eliza Wolf") };
        Dictionary<long, string?> known = new() { [18279837] = "Willow Eliza Wolf", [19457624] = "Willow Eliza Wolf" };

        Assert.Equal([(18279837L, 19457624L)], BusMonitor.Renumbered(roster, known));
    }

    [Fact]
    public void AFileFromBeforeNamesWereKeptMovesOnlyWhenUnambiguous()
    {
        Dictionary<long, string?> known = new() { [18279837] = null };

        Assert.Single(BusMonitor.Renumbered(
            new Dictionary<long, Student> { [19457624] = Rider(19457624, "Willow") }, known));

        // Two children on the account: which one it was is a guess, so none.
        Assert.Empty(BusMonitor.Renumbered(
            new Dictionary<long, Student> { [1] = Rider(1, "Willow"), [2] = Rider(2, "Sam") }, known));
    }

    [Fact]
    public void ADifferentChildIsNeverGivenSomeoneElsesHistory()
    {
        Dictionary<long, Student> roster = new() { [2] = Rider(2, "Sam") };
        Dictionary<long, string?> known = new() { [1] = "Willow" };

        Assert.Empty(BusMonitor.Renumbered(roster, known));
    }

    [Fact]
    public void RenumberingMergesTheOldHistoryUnderTheNewId()
    {
        PredictionEngine engine = new(Clock);
        engine.LoadHistory(18279837, [Arrival(23, Run.Am), Arrival(23, Run.Pm), Arrival(26, Run.Pm)]);
        engine.LoadHistory(19457624, [Arrival(27, Run.Am)]);

        Assert.True(engine.Renumber(18279837, 19457624));

        Assert.Equal(4, engine.ArrivalsFor(19457624).Count);
        Assert.Empty(engine.ArrivalsFor(18279837));
        Assert.Equal([19457624L], engine.Riders);
    }

    [Fact]
    public void TheNameSurvivesTheFile()
    {
        string json = HistoryStore.Serialise([new StoredRider { ChildId = 7, Name = "Willow Eliza Wolf" }]);

        Assert.Equal("Willow Eliza Wolf", Assert.Single(HistoryStore.Deserialise(json)).Name);
    }
}
