// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core.Prediction;

namespace WhenWillTheBus.Core.Tests;

public sealed class GeoTests
{
    [Fact]
    public void Distance_MatchesAKnownSeparation()
    {
        // One degree of latitude is a little over 69 statute miles.
        double miles = Geo.DistanceMiles(40.0, -74.0, 41.0, -74.0);

        Assert.InRange(miles, 69.0, 69.2);
    }

    [Fact]
    public void Distance_IsZeroForTheSamePoint() =>
        Assert.Equal(0.0, Geo.DistanceMiles(40.735, -74.02, 40.735, -74.02), precision: 9);

    [Theory]
    [InlineData(41.0, -74.0, 0.0)]     // due north
    [InlineData(40.0, -73.0, 90.0)]    // due east
    [InlineData(39.0, -74.0, 180.0)]   // due south
    [InlineData(40.0, -75.0, 270.0)]   // due west
    public void Bearing_PointsTheRightWay(double toLatitude, double toLongitude, double expected)
    {
        double bearing = Geo.BearingDegrees(40.0, -74.0, toLatitude, toLongitude);

        Assert.InRange(Geo.TurnBetween(bearing, expected), 0.0, 0.5);
    }

    [Fact]
    public void Bearing_IsNeverNegative()
    {
        // Atan2 returns negative angles for westward headings; a compass does not.
        double bearing = Geo.BearingDegrees(40.0, -74.0, 40.0, -75.0);

        Assert.InRange(bearing, 0.0, 360.0);
    }

    [Theory]
    [InlineData(0.0, 350.0, 10.0)]     // across the 360 wrap
    [InlineData(350.0, 0.0, 10.0)]
    [InlineData(90.0, 270.0, 180.0)]   // a U-turn
    [InlineData(45.0, 45.0, 0.0)]
    public void TurnBetween_TakesTheShorterWayRound(double first, double second, double expected) =>
        Assert.Equal(expected, Geo.TurnBetween(first, second), precision: 6);

    /// <summary>
    /// §3.2. A bus idling at a stop still jitters by a few metres, and the
    /// bearing of that jitter is noise pointing in a random direction. Null is
    /// the right answer, and it is a meaningful one: it says the bus was not
    /// going anywhere, which the matcher treats quite differently from a claim
    /// about direction.
    /// </summary>
    [Fact]
    public void Heading_IsNullWhileTheBusIsOnlyJittering()
    {
        // Five metres of wander, well under the 0.02 mi (32 m) floor.
        GeoPoint[] jitter =
        [
            new(40.73500, -74.02000),
            new(40.73503, -74.02002),
            new(40.73499, -74.01998),
            new(40.73501, -74.02001),
        ];

        Assert.Null(Geo.HeadingOf(jitter));
    }

    [Fact]
    public void Heading_TakesTheBearingFromTheLastFixThatGenuinelyMoved()
    {
        // Moved east, then sat still. The bearing must still read east, not the
        // direction of the last two jittering fixes.
        GeoPoint[] points =
        [
            new(40.73500, -74.02000),
            new(40.73500, -74.01000),
            new(40.73501, -74.01001),
            new(40.73500, -74.01000),
        ];

        double? heading = Geo.HeadingOf(points);

        Assert.NotNull(heading);
        Assert.InRange(Geo.TurnBetween(heading.Value, 90.0), 0.0, 5.0);
    }

    [Fact]
    public void Heading_NeedsTwoFixes() => Assert.Null(Geo.HeadingOf([new GeoPoint(40.735, -74.02)]));

    [Fact]
    public void AlongSegment_InterpolatesAgeAtTheMidpoint()
    {
        TrackPoint start = new(600, 40.73500, -74.02000);
        TrackPoint end = new(570, 40.73500, -74.01000);

        (double gap, double age) = Geo.AlongSegment(40.73500, -74.01500, start, end);

        Assert.InRange(gap, 0.0, 0.001);
        Assert.InRange(age, 584.9, 585.1);
    }

    [Fact]
    public void AlongSegment_ClampsBeyondEitherEnd()
    {
        TrackPoint start = new(600, 40.73500, -74.02000);
        TrackPoint end = new(570, 40.73500, -74.01000);

        // Well past the eastern end: the meeting point is the end itself.
        (double _, double age) = Geo.AlongSegment(40.73500, -74.00000, start, end);

        Assert.Equal(570.0, age, precision: 6);
    }

    [Fact]
    public void AlongSegment_HandlesAZeroLengthSegment()
    {
        TrackPoint stationary = new(600, 40.73500, -74.02000);

        (double gap, double age) = Geo.AlongSegment(40.73500, -74.01000, stationary, stationary);

        Assert.Equal(600.0, age, precision: 6);
        Assert.InRange(gap, 0.5, 0.6);
    }
}
