// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core.Prediction;

namespace WhenWillTheBus.Core.Tests;

public sealed class RouteMatcherTests
{
    /// <summary>
    /// §3.1 and §14. The bus spends nearly all its time BETWEEN the recorded
    /// fixes, so scoring only at the samples asks a question any matcher gets
    /// right. Queried at the midpoints of legs the bus actually drove,
    /// point-to-segment is exact and nearest-sample is half a poll out.
    /// </summary>
    [Fact]
    public void PointToSegment_IsExactAtMidpoints_WhereNearestSampleIsHalfAPollOut()
    {
        IReadOnlyList<TrackPoint> track = RouteFixtures.StraightRun();

        List<double> segmentErrors = [];
        List<double> sampleErrors = [];

        for (int index = 0; index < track.Count - 1; index++)
        {
            TrackPoint start = track[index];
            TrackPoint end = track[index + 1];
            double midLatitude = (start.Latitude + end.Latitude) / 2;
            double midLongitude = (start.Longitude + end.Longitude) / 2;

            // The bus drove this leg at a steady pace, so halfway along it had
            // exactly the average of the two recorded remainders left.
            double truth = (start.SecondsBeforeArrival + end.SecondsBeforeArrival) / 2.0;

            int? bySegment = RouteMatcher.NearestRemaining(track, midLatitude, midLongitude, elapsed: null);
            int? bySample = RouteFixtures.NearestSampleRemaining(
                track, midLatitude, midLongitude, RouteMatcher.MatchRadiusMiles);

            Assert.NotNull(bySegment);
            Assert.NotNull(bySample);
            segmentErrors.Add(Math.Abs(bySegment.Value - truth));
            sampleErrors.Add(Math.Abs(bySample.Value - truth));
        }

        // Exact to the rounding of a whole second.
        Assert.True(segmentErrors.Max() <= 1.0, $"worst segment error {segmentErrors.Max():F2}s");

        // Nearest-sample can only ever answer with an endpoint, so at a midpoint
        // it is wrong by half the sample spacing every single time.
        Assert.True(sampleErrors.Min() >= 14.0, $"best sample error {sampleErrors.Min():F2}s");
    }

    /// <summary>
    /// §3.2. The same junction, driven outbound and homeward. The outbound pass
    /// is deliberately the NEARER of the two, so distance alone always answers
    /// "outbound" — only the heading can tell them apart.
    /// </summary>
    [Theory]
    [InlineData(90.0, RouteFixtures.OutboundRemaining)]   // heading east
    [InlineData(270.0, RouteFixtures.HomewardRemaining)]  // heading west, 3.5 minutes later
    public void Heading_TellsTheTwoPassesOfACrossingApart(double heading, int expected)
    {
        IReadOnlyList<TrackPoint> track = RouteFixtures.CrossingRoute();

        int? remaining = RouteMatcher.NearestRemaining(
            track,
            RouteFixtures.CrossingQueryLatitude,
            RouteFixtures.CrossingLongitude,
            elapsed: null,
            heading: heading);

        Assert.Equal(expected, remaining);
    }

    /// <summary>
    /// The other half of the same mechanism, pinned so it cannot quietly
    /// change: DISTANCE LEADS. With no heading to go on, the nearer pass wins
    /// whichever way the bus is actually travelling — which is precisely why
    /// the heading filter has to exist.
    /// </summary>
    [Fact]
    public void WithoutAHeading_TheNearerPassAlwaysWins()
    {
        IReadOnlyList<TrackPoint> track = RouteFixtures.CrossingRoute();

        int? remaining = RouteMatcher.NearestRemaining(
            track,
            RouteFixtures.CrossingQueryLatitude,
            RouteFixtures.CrossingLongitude,
            elapsed: null);

        Assert.Equal(RouteFixtures.OutboundRemaining, remaining);
    }

    /// <summary>
    /// §3.2, the half that is easy to get wrong. A sample with no heading of its
    /// own is a bus that was STANDING STILL there. That is a real place on the
    /// route and keeps its claim; only a sample known to be going the other way
    /// is refused.
    /// </summary>
    [Fact]
    public void SampleWithNoHeadingOfItsOwn_KeepsItsClaim()
    {
        // A bus that sat still for three polls, then left westward. The
        // stationary samples have no bearing of their own but are certainly a
        // place the route goes.
        IReadOnlyList<TrackPoint> track =
        [
            new(300, 40.75000, -74.03000),
            new(270, 40.75000, -74.03000),
            new(240, 40.75000, -74.03000),
            new(210, 40.75000, -74.03100),
        ];

        // Querying from the opposite direction to the one the bus eventually
        // took. The standing samples must still answer.
        int? remaining = RouteMatcher.NearestRemaining(
            track, latitude: 40.75000, longitude: -74.03000, elapsed: null, heading: 90.0);

        Assert.NotNull(remaining);
    }

    /// <summary>
    /// §3.3 and §11.5. A bus parked at the depot matches a past journey's whole
    /// parked block, whose two ends are half an hour apart. The position is
    /// real; it says nothing about progress. Answering anyway is how a parked
    /// bus came out 31 minutes adrift on 15 Sep.
    /// </summary>
    [Fact]
    public void AmbiguousSpread_RefusesToAnswerRatherThanGuess()
    {
        // The same spot, occupied at the very start of a past journey and again
        // thirty-five minutes later: a depot the route passes twice.
        IReadOnlyList<TrackPoint> track =
        [
            new(2400, 40.76000, -74.06000),
            new(2370, 40.76001, -74.06001),
            new(600, 40.76000, -74.06000),
            new(570, 40.76001, -74.06002),
        ];

        int? remaining = RouteMatcher.NearestRemaining(
            track, latitude: 40.76000, longitude: -74.06000, elapsed: null);

        Assert.Null(remaining);
    }

    /// <summary>
    /// The guard must not be so eager that it refuses an ordinary wait at a
    /// stop. Measured spreads while moving were 0 to 2.5 minutes.
    /// </summary>
    [Fact]
    public void ModestSpread_StillAnswers()
    {
        IReadOnlyList<TrackPoint> track =
        [
            new(600, 40.76000, -74.06000),
            new(570, 40.76001, -74.06001),
            new(540, 40.76000, -74.06000),
            new(510, 40.76001, -74.06002),
        ];

        Assert.NotNull(RouteMatcher.NearestRemaining(track, 40.76000, -74.06000, elapsed: null));
    }

    [Fact]
    public void SingleSampleTrack_HasNoSegment_SoFallsBackToAPointMatch()
    {
        IReadOnlyList<TrackPoint> track = [new(450, 40.77000, -74.07000)];

        Assert.Equal(450, RouteMatcher.NearestRemaining(track, 40.77001, -74.07001, elapsed: null));
        Assert.Null(RouteMatcher.NearestRemaining(track, 40.90000, -74.07000, elapsed: null));
    }

    /// <summary>
    /// A journey that never came near this spot has nothing to say about it —
    /// a detour, a substitute on another route, a stretch it did not record.
    /// Saying so is the point.
    /// </summary>
    [Fact]
    public void JourneyThatNeverCameNear_SaysNothing()
    {
        IReadOnlyList<TrackPoint> track = RouteFixtures.StraightRun();

        Assert.Null(RouteMatcher.NearestRemaining(track, 41.20000, -74.90000, elapsed: null));
    }

    [Fact]
    public void EmptyTrack_SaysNothing()
    {
        Assert.Null(RouteMatcher.NearestRemaining([], 40.735, -74.02, elapsed: null));
    }
}
