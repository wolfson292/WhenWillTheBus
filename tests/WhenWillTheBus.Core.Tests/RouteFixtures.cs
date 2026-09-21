// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core.Prediction;

namespace WhenWillTheBus.Core.Tests;

/// <summary>
/// Synthetic journeys shaped like the real ones: sampled every thirty seconds,
/// and crossing themselves the way a school run does.
/// </summary>
internal static class RouteFixtures
{
    /// <summary>Roughly a quarter of a mile of longitude at this latitude.</summary>
    private const double LegLongitude = 0.0045;

    public const double OutboundLatitude = 40.73500;
    public const double HomewardLatitude = 40.73520;

    private const double WestEnd = -74.02000;
    private const int Legs = 6;

    /// <summary>
    /// A longitude the bus passes TWICE on <see cref="CrossingRoute"/>: once
    /// outbound and once homeward. Derived rather than written down, because a
    /// hand-copied coordinate silently stops being the crossing point the
    /// moment the fixture's spacing changes.
    /// </summary>
    public const double CrossingLongitude = WestEnd + (2 * LegLongitude);

    /// <summary>Just SOUTH of the outbound line, so outbound is always the nearer pass.</summary>
    public const double CrossingQueryLatitude = OutboundLatitude - 0.00005;

    /// <summary>What the outbound pass had left when it was at <see cref="CrossingLongitude"/>.</summary>
    public const int OutboundRemaining = 1200 - (2 * 30);

    /// <summary>What the homeward pass had left at the same longitude, 3.5 minutes later.</summary>
    public const int HomewardRemaining = 1020 - (3 * 30);

    /// <summary>
    /// A route that runs east, turns, and comes back west a few metres to the
    /// north — so the same junction is passed twice, about ninety seconds apart,
    /// heading opposite ways. This is the case heading exists to resolve.
    /// </summary>
    /// <remarks>
    /// The homeward pass is laid slightly NORTH of the outbound one and the test
    /// query sits slightly SOUTH of centre, so the nearer of the two passes is
    /// always the outbound one. A matcher that ranks on distance alone therefore
    /// answers with the outbound age no matter which way the bus is going, and
    /// only the heading filter can tell them apart.
    /// </remarks>
    public static IReadOnlyList<TrackPoint> CrossingRoute(
        double outboundLatitude = OutboundLatitude,
        double homewardLatitude = HomewardLatitude)
    {
        List<TrackPoint> track = [];


        // Outbound, heading east. Ages count DOWN towards the arrival.
        for (int step = 0; step < Legs; step++)
        {
            track.Add(new TrackPoint(1200 - (step * 30), outboundLatitude, WestEnd + (step * LegLongitude)));
        }

        // Homeward, retracing the SAME longitudes westward a few metres north,
        // so every outbound point has a homeward twin about 3.5 minutes later.
        double turnaround = WestEnd + ((Legs - 1) * LegLongitude);
        for (int step = 0; step < Legs; step++)
        {
            track.Add(new TrackPoint(1020 - (step * 30), homewardLatitude, turnaround - (step * LegLongitude)));
        }

        return track;
    }

    /// <summary>A plain westward run with no self-crossing, sampled every 30 s.</summary>
    public static IReadOnlyList<TrackPoint> StraightRun(int samples = 20, int spacingSeconds = 30)
    {
        List<TrackPoint> track = [];
        for (int step = 0; step < samples; step++)
        {
            track.Add(new TrackPoint(
                (samples - 1 - step) * spacingSeconds,
                40.74000,
                -74.05000 + (step * LegLongitude)));
        }

        return track;
    }

    /// <summary>
    /// The nearest-SAMPLE matcher the reference implementation started with,
    /// kept here only so the tests can show what point-to-segment bought.
    /// </summary>
    public static int? NearestSampleRemaining(
        IReadOnlyList<TrackPoint> track,
        double latitude,
        double longitude,
        double radiusMiles)
    {
        int? best = null;
        double bestGap = double.MaxValue;

        foreach (TrackPoint point in track)
        {
            double gap = Geo.DistanceMiles(latitude, longitude, point.Latitude, point.Longitude);
            if (gap <= radiusMiles && gap < bestGap)
            {
                bestGap = gap;
                best = point.SecondsBeforeArrival;
            }
        }

        return best;
    }
}
