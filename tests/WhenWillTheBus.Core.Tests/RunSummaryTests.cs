// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Prediction;

namespace WhenWillTheBus.Core.Tests;

public sealed class RunSummaryTests
{
    private static readonly LocalClock Clock = new(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
    private const double Mile = 1.0 / 69.0;

    /// <summary>A run north along a road, nudged east, arriving <paramref name="day"/> at 08:00 local.</summary>
    private static RunArrival Run(int day, double eastMiles = 0, int minutesLate = 0)
    {
        double east = eastMiles / (69.0 * Math.Cos(26.5 * Math.PI / 180));
        List<TrackPoint> track = [];
        for (int step = 0; step <= 20; step++)
        {
            track.Add(new TrackPoint((20 - step) * 30, 26.5 + (step * 0.1 * Mile), -81.85 + east));
        }

        return new RunArrival
        {
            Run = Model.Run.Am,
            Arrival = new DateTimeOffset(2026, 10, day, 12, minutesLate, 0, TimeSpan.Zero),
            ClosestMiles = 0.1,
            Track = track,
        };
    }

    /// <summary>
    /// THE PRIVACY LINE. This text is what leaves the house; nothing in it may
    /// say where the route is.
    /// </summary>
    [Fact]
    public void TheDescriptionCarriesNoCoordinates()
    {
        List<RunArrival> history = [Run(19), Run(20), Run(21), Run(22)];
        string text = RunSummary.Of(history[^1], history, Clock).Describe();

        Assert.DoesNotContain("26.5", text);
        Assert.DoesNotContain("81.8", text);
        Assert.False(Regex.IsMatch(text, @"-?\d{2,3}\.\d{3,}"), text);
    }

    [Fact]
    public void TheUsualRoadIsAllOnUsualRoads()
    {
        List<RunArrival> history = [Run(19), Run(20), Run(21), Run(22)];
        RunSummary summary = RunSummary.Of(history[^1], history, Clock);

        Assert.Equal(1.0, summary.OnUsualRoads!.Value, 2);
        Assert.Equal(3, summary.Compared);
        Assert.Equal(new TimeOnly(8, 0), summary.UsualArrival);
        Assert.Equal(2.0, summary.Miles!.Value, 1);
    }

    [Fact]
    public void ADetourShowsAsLittleOnUsualRoads()
    {
        List<RunArrival> history = [Run(19), Run(20), Run(21), Run(22, eastMiles: 0.5, minutesLate: 25)];
        RunSummary summary = RunSummary.Of(history[^1], history, Clock);

        Assert.True(summary.OnUsualRoads < 0.1, $"{summary.OnUsualRoads}");
        Assert.Equal(new TimeOnly(8, 25), summary.Arrived);
    }

    /// <summary>Only runs of the same kind are the usual: a morning is not judged against afternoons.</summary>
    [Fact]
    public void AfternoonsAreNotTheUsualForAMorning()
    {
        RunArrival afternoon = Run(20, eastMiles: 3) with { Run = Model.Run.Pm };
        List<RunArrival> history = [afternoon, Run(21)];

        Assert.Equal(0, RunSummary.Of(history[^1], history, Clock).Compared);
    }
}
