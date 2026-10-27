// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core.Prediction;

namespace WhenWillTheBus.Core.Tests;

public sealed class RouteHeatTests
{
    // Around Bonita Springs; 0.0145 degrees of latitude is about a mile.
    private const double Latitude = 26.52;
    private const double Longitude = -81.86;
    private const double Mile = 1.0 / 69.0;

    /// <summary>A road running north, a fix every tenth of a mile, nudged east by <paramref name="eastMiles"/>.</summary>
    private static List<GeoPoint> North(double fromMiles, double toMiles, double eastMiles = 0)
    {
        double east = eastMiles / (69.0 * Math.Cos(Latitude * Math.PI / 180));
        List<GeoPoint> points = [];
        for (double miles = fromMiles; miles <= toMiles + 1e-9; miles += 0.1)
        {
            points.Add(new GeoPoint(Latitude + (miles * Mile), Longitude + east));
        }

        return points;
    }

    private static RouteHeatLevel LevelNear(IReadOnlyList<RouteHeatSegment> segments, GeoPoint at) =>
        segments
            .Where(segment => segment.Path.Any(point => Geo.DistanceMiles(point, at) < 0.01))
            .Max(segment => segment.Level);

    /// <summary>
    /// The point of it: ten days on the usual road and one detour must not
    /// look alike, which is what a stack of faint lines made them.
    /// </summary>
    [Fact]
    public void TheUsualRoadIsMostDaysAndAOneOffDetourIsRarely()
    {
        List<IReadOnlyList<GeoPoint>> routes = [.. Enumerable.Range(0, 10).Select(_ => (IReadOnlyList<GeoPoint>)North(0, 2))];

        // Half a mile east of the usual road, once.
        routes.Add(North(0, 2, eastMiles: 0.5));

        IReadOnlyList<RouteHeatSegment> heat = RouteHeat.Build(routes);

        Assert.Equal(RouteHeatLevel.MostDays, LevelNear(heat, new GeoPoint(Latitude + Mile, Longitude)));

        double east = 0.5 / (69.0 * Math.Cos(Latitude * Math.PI / 180));
        Assert.Equal(RouteHeatLevel.Rarely, LevelNear(heat, new GeoPoint(Latitude + Mile, Longitude + east)));
    }

    /// <summary>
    /// GPS wanders by a lane or so, and fixes thirty seconds apart never land
    /// in the same place twice. Neither may split one road into several.
    /// </summary>
    [Fact]
    public void ALanesWidthOfWanderIsStillTheSameRoad()
    {
        List<IReadOnlyList<GeoPoint>> routes =
        [
            North(0, 2),
            North(0.05, 2.05, eastMiles: 0.01),
            North(0.02, 2.02, eastMiles: -0.01),
            North(0.07, 2.07, eastMiles: 0.005),
        ];

        IReadOnlyList<RouteHeatSegment> heat = RouteHeat.Build(routes);

        Assert.Equal(RouteHeatLevel.MostDays, LevelNear(heat, new GeoPoint(Latitude + Mile, Longitude)));
    }

    /// <summary>
    /// Counted per journey. A bus that waits at a stop leaves a pile of fixes
    /// there, and those must not make the stop the busiest place on the map.
    /// </summary>
    [Fact]
    public void DwellingAtAStopCountsAsOneJourneyThere()
    {
        List<GeoPoint> dwelling = North(0, 1);
        GeoPoint stop = dwelling[5];
        dwelling.InsertRange(5, Enumerable.Repeat(stop, 20));

        List<IReadOnlyList<GeoPoint>> routes = [dwelling, .. Enumerable.Range(0, 4).Select(_ => (IReadOnlyList<GeoPoint>)North(2, 3))];

        IReadOnlyList<RouteHeatSegment> heat = RouteHeat.Build(routes);

        Assert.All(
            heat.Where(segment => segment.Path.Any(point => Geo.DistanceMiles(point, stop) < 0.01)),
            segment => Assert.Equal(1, segment.Journeys));
    }

    /// <summary>A lost signal is a jump between two fixes, not a road; no line may cross it.</summary>
    [Fact]
    public void ALostSignalIsNotDrawnAsAStraightLine()
    {
        List<GeoPoint> route = [.. North(0, 0.5), .. North(3, 3.5)];

        IReadOnlyList<RouteHeatSegment> heat = RouteHeat.Build([route]);

        GeoPoint middle = new(Latitude + (1.75 * Mile), Longitude);
        Assert.DoesNotContain(heat, segment => segment.Path.Any(point => Geo.DistanceMiles(point, middle) < 0.5));
    }

    /// <summary>The usual road is drawn last, so where a rare line crosses it the usual one shows.</summary>
    [Fact]
    public void SegmentsComeRarestFirst()
    {
        List<IReadOnlyList<GeoPoint>> routes = [.. Enumerable.Range(0, 10).Select(_ => (IReadOnlyList<GeoPoint>)North(0, 2))];
        routes.Add(North(0, 2, eastMiles: 0.5));

        IReadOnlyList<RouteHeatLevel> order = [.. RouteHeat.Build(routes).Select(segment => segment.Level)];

        Assert.Equal(order.Order(), order);
        Assert.Contains(RouteHeatLevel.Rarely, order);
        Assert.Contains(RouteHeatLevel.MostDays, order);
    }

    /// <summary>
    /// Every overlay costs MapKit main-thread time, and slivers of colour a few
    /// metres long made up most of the 175 that got the app killed on 26 Oct.
    /// A side street the usual road crosses must not cut it into pieces.
    /// </summary>
    [Fact]
    public void ACrossingSideStreetDoesNotCutTheUsualRoadIntoSlivers()
    {
        List<IReadOnlyList<GeoPoint>> routes = [.. Enumerable.Range(0, 10).Select(_ => (IReadOnlyList<GeoPoint>)North(0, 2))];

        // Three journeys that join the usual road for a moment and leave it.
        double west = 0.3 / (69.0 * Math.Cos(Latitude * Math.PI / 180));
        foreach (int _ in Enumerable.Range(0, 3))
        {
            routes.Add(
            [
                new GeoPoint(Latitude + Mile, Longitude - west),
                new GeoPoint(Latitude + Mile, Longitude),
                new GeoPoint(Latitude + Mile, Longitude + west),
            ]);
        }

        IReadOnlyList<RouteHeatSegment> heat = RouteHeat.Build(routes);

        Assert.All(heat, segment =>
        {
            double miles = 0;
            for (int index = 1; index < segment.Path.Count; index++)
            {
                miles += Geo.DistanceMiles(segment.Path[index - 1], segment.Path[index]);
            }

            Assert.True(miles >= RouteHeat.MinimumStretchMiles, $"{segment.Level} stretch of {miles:F3} mi");
        });

        // One line per usual journey, not one per side of the crossing.
        Assert.Equal(10, heat.Count(segment => segment.Level == RouteHeatLevel.MostDays));
    }

    [Theory]
    [InlineData(10, 10, RouteHeatLevel.MostDays)]
    [InlineData(5, 10, RouteHeatLevel.Often)]
    [InlineData(2, 10, RouteHeatLevel.Sometimes)]
    [InlineData(1, 10, RouteHeatLevel.Rarely)]
    [InlineData(0, 0, RouteHeatLevel.Rarely)]
    public void LevelsFollowTheShareOfJourneys(int journeys, int total, RouteHeatLevel expected) =>
        Assert.Equal(expected, RouteHeat.LevelFor(journeys, total));

    [Fact]
    public void NothingToDrawIsNothing() => Assert.Empty(RouteHeat.Build([]));
}
