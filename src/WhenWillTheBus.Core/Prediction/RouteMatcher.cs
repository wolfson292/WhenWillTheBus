// SPDX-License-Identifier: GPL-3.0-or-later

namespace WhenWillTheBus.Core.Prediction;

/// <summary>
/// Work out how far through its route a bus is, by matching where it has been.
/// </summary>
/// <remarks>
/// Distance to the rider's stop is a lossy projection of a school run. The bus
/// is not driving towards the stop: it is driving a route, serving other
/// children, turning around in cul-de-sacs, and at times moving directly away
/// from the stop while making perfect progress. Measured as straight-line
/// distance, a U-turn looks like a setback and a loitering bus looks like one
/// that will never arrive — an estimate built on it slides forward with the
/// clock and never converges. Observed on 14 Sep: twelve minutes later over
/// twelve minutes, while the bus worked stops four miles out.
///
/// The route, though, is very nearly the same every day. So the useful question
/// is not "how far is the bus from the stop" but "where on the route is it, and
/// how long did that take last time". Matching today's position against the
/// positions past journeys passed through answers that directly: a U-turn
/// matches a U-turn, a pause outside another school matches the same pause, and
/// a bus genuinely running ahead matches a point that came late before.
///
/// Observed on 11 Sep, four samples thirty seconds apart: west, west,
/// stationary, then back east. Nothing about that is legible as a distance. As
/// a position on a route it is unmistakable, and it happened thirteen minutes
/// before the bus reached the stop.
/// </remarks>
public static class RouteMatcher
{
    /// <summary>
    /// How close a past position must be to count as the same place on the
    /// route. Wide enough to absorb GPS scatter and a bus stopping on either
    /// side of a road, tight enough that the outbound and homeward passes
    /// through the same junction stay distinguishable.
    /// </summary>
    public const double MatchRadiusMiles = 0.25;

    /// <summary>
    /// How far two headings may differ and still count as the same way along
    /// the road. Ninety degrees splits "onward" from "back the way it came"
    /// while leaving room for a bend taken between fixes; the passes either
    /// side of a U-turn are near enough 180 apart.
    /// </summary>
    public const double HeadingToleranceDegrees = 90.0;

    /// <summary>
    /// How far apart the answers from one place may be before that place is
    /// admitted to not have an answer.
    /// </summary>
    /// <remarks>
    /// A bus parked at the depot matches the whole of a past journey's parked
    /// block, and the two ends of that block are half an hour apart: the
    /// position is real, and it says nothing about progress.
    ///
    /// Measured on the 15 Sep morning run against 14 Sep. While the bus was
    /// moving, the matching samples spanned 0.0 to 2.5 minutes. While it was
    /// parked, 34.5 to 35.5. There is no borderline case in between — position
    /// either pins the journey down or misses by an order of magnitude — so
    /// five minutes sits clear of a genuine wait at a stop and nowhere near
    /// the depot.
    /// </remarks>
    public const int AmbiguousSpreadSeconds = 300;

    /// <summary>The fewest past journeys that may carry a route estimate.</summary>
    public const int MinimumRouteSamples = 2;

    /// <summary>
    /// Return how long was left, when a past journey was where the bus is now,
    /// or null when that journey has nothing to say about this spot.
    /// </summary>
    /// <param name="track">
    /// One past journey, oldest sample first. Each sample carries how long that
    /// journey still had to run when it was there.
    /// </param>
    /// <param name="elapsed">
    /// How long today's journey has been running, in seconds. Used ONLY to
    /// break ties between same-direction passes; null when unknown.
    /// </param>
    /// <param name="radiusMiles">How close a past position has to be to count.</param>
    /// <param name="heading">
    /// Which way the bus is travelling now, in degrees, or null if it is
    /// standing still or has only one fix.
    /// </param>
    /// <remarks>
    /// Returns null when a journey never came near this spot, which simply
    /// means it has nothing to say about where the bus is now — a detour, a
    /// substitute bus on another route, or a stretch that journey did not
    /// record. Saying so is better than guessing.
    ///
    /// A route crosses itself: the same junction can be passed on the way out
    /// and again on the way back, and the two passes want different answers —
    /// often ninety seconds apart at the very same spot. <paramref name="heading"/>
    /// is the strongest thing available for telling them apart, so past samples
    /// pointing the wrong way are dropped outright.
    ///
    /// Direction beats elapsed time at this because it owes nothing to the
    /// clock. Elapsed compares today's running time against a past journey's,
    /// which assumes today is going roughly like that journey did — exactly the
    /// assumption the estimate exists to test. A bus ten minutes down drifts
    /// against every past sample equally and the tie-break stops discriminating
    /// at the moment it matters most. So elapsed stays, demoted to separating
    /// same-direction passes.
    /// </remarks>
    public static int? NearestRemaining(
        IReadOnlyList<TrackPoint> track,
        double latitude,
        double longitude,
        int? elapsed,
        double radiusMiles = MatchRadiusMiles,
        double? heading = null)
    {
        if (track.Count == 0)
        {
            return null;
        }

        // How long the track covers, so a sample's own elapsed time can be read
        // off it: the first sample is the furthest from arrival.
        int span = track[0].SecondsBeforeArrival;

        // Distance LEADS; elapsed only separates ties. Elapsed is measured from
        // the first sample of each track, and those starts are not comparable
        // between journeys: a track begins when the approach window opens or
        // when the rider scans on, and the window itself moves as the learned
        // centre does. Sorting on it first made the primary key the least
        // reliable number available.
        List<(double Gap, double Drift, double Age)> matches = [];

        for (int index = 0; index < track.Count - 1; index++)
        {
            (double gap, double age) = Geo.AlongSegment(latitude, longitude, track[index], track[index + 1]);
            if (gap > radiusMiles)
            {
                continue;
            }

            if (heading is not null)
            {
                double? was = SampleHeading(track, index + 1);

                // A sample with no heading of its own is a bus that was
                // standing still there, which is a real place on the route and
                // keeps its claim. Only a sample KNOWN to be going the other
                // way is refused.
                if (was is not null && Geo.TurnBetween(heading.Value, was.Value) > HeadingToleranceDegrees)
                {
                    continue;
                }
            }

            double drift = elapsed is null ? 0.0 : Math.Abs((span - age) - elapsed.Value);
            matches.Add((gap, drift, age));
        }

        if (matches.Count == 0)
        {
            // A single-sample track has no segment to project onto.
            if (track.Count == 1)
            {
                TrackPoint only = track[0];
                if (Geo.DistanceMiles(latitude, longitude, only.Latitude, only.Longitude) <= radiusMiles)
                {
                    return only.SecondsBeforeArrival;
                }
            }

            return null;
        }

        // If this place meant wildly different things at different times of a
        // past journey, it does not locate today's. Answering anyway is worse
        // than not answering: the caller has a historical estimate to fall back
        // on that is honestly vague, and replacing it with a confident wrong
        // number is how a parked bus came out 31 minutes adrift on 15 Sep.
        double youngest = matches.Min(match => match.Age);
        double oldest = matches.Max(match => match.Age);
        if (oldest - youngest > AmbiguousSpreadSeconds)
        {
            return null;
        }

        (double _, double _, double bestAge) = matches
            .OrderBy(match => match.Gap)
            .ThenBy(match => match.Drift)
            .ThenBy(match => match.Age)
            .First();

        return (int)Math.Round(bestAge, MidpointRounding.ToEven);
    }

    /// <summary>Return which way a past journey was travelling at one of its samples.</summary>
    private static double? SampleHeading(IReadOnlyList<TrackPoint> track, int index)
    {
        GeoPoint[] upToHere = new GeoPoint[index + 1];
        for (int i = 0; i <= index; i++)
        {
            upToHere[i] = track[i].Position;
        }

        return Geo.HeadingOf(upToHere);
    }
}
