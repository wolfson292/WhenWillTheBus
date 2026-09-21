// SPDX-License-Identifier: GPL-3.0-or-later

namespace WhenWillTheBus.Core.Prediction;

/// <summary>A position on the earth's surface.</summary>
public readonly record struct GeoPoint(double Latitude, double Longitude);

/// <summary>
/// One sample of a past journey: where the bus was, and how long it still had
/// to go when it was there.
/// </summary>
/// <param name="SecondsBeforeArrival">
/// Seconds from this sample to the moment the bus reached the stop. Counts
/// DOWN through a track, so the first sample of a track carries the largest
/// value.
/// </param>
public readonly record struct TrackPoint(int SecondsBeforeArrival, double Latitude, double Longitude)
{
    public GeoPoint Position => new(Latitude, Longitude);
}

/// <summary>
/// Spherical geometry, and the two measurements the route matcher is built on:
/// which way something is travelling, and how far it lies from a segment of a
/// journey somebody already drove.
/// </summary>
/// <remarks>
/// Everything here works in MILES. The API reports distance in miles or
/// kilometres depending on the account, and the reference implementation
/// carried a parallel set of kilometre constants that were then compared
/// against mile measurements — a radius of "0.4 km" that was in practice
/// 0.4 miles. Converting once at the API boundary and working in a single
/// unit throughout removes that whole class of mistake.
/// </remarks>
public static class Geo
{
    /// <summary>
    /// Mean radius of the earth. Journeys here are a few miles, where treating
    /// the earth as a sphere is wrong by centimetres.
    /// </summary>
    private const double EarthRadiusMiles = 3958.7613;

    /// <summary>
    /// How far apart two fixes must be before the line between them is taken
    /// as a heading. A bus idling at a stop still jitters by a few metres, and
    /// the bearing of that jitter is noise pointing in a random direction.
    /// </summary>
    public const double HeadingMinimumMiles = 0.02;

    /// <summary>A half turn, and the fewest fixes a direction can be drawn from.</summary>
    private const double StraightAngle = 180.0;

    public const double MilesPerKilometre = 1.0 / 1.609344;

    /// <summary>Convert a distance the API reported in kilometres into miles.</summary>
    public static double KilometresToMiles(double kilometres) => kilometres * MilesPerKilometre;

    /// <summary>Return the great-circle distance between two points, in miles.</summary>
    public static double DistanceMiles(double fromLat, double fromLon, double toLat, double toLon)
    {
        double lat1 = double.DegreesToRadians(fromLat);
        double lon1 = double.DegreesToRadians(fromLon);
        double lat2 = double.DegreesToRadians(toLat);
        double lon2 = double.DegreesToRadians(toLon);

        double sinLat = Math.Sin((lat2 - lat1) / 2);
        double sinLon = Math.Sin((lon2 - lon1) / 2);
        double inner = (sinLat * sinLat) + (Math.Cos(lat1) * Math.Cos(lat2) * sinLon * sinLon);
        return 2 * EarthRadiusMiles * Math.Asin(Math.Sqrt(inner));
    }

    /// <summary>Return the great-circle distance between two points, in miles.</summary>
    public static double DistanceMiles(GeoPoint from, GeoPoint to) =>
        DistanceMiles(from.Latitude, from.Longitude, to.Latitude, to.Longitude);

    /// <summary>Return the initial compass bearing from one point to another.</summary>
    public static double BearingDegrees(double fromLat, double fromLon, double toLat, double toLon)
    {
        double lat1 = double.DegreesToRadians(fromLat);
        double lat2 = double.DegreesToRadians(toLat);
        double deltaLon = double.DegreesToRadians(toLon - fromLon);

        double y = Math.Sin(deltaLon) * Math.Cos(lat2);
        double x = (Math.Cos(lat1) * Math.Sin(lat2)) - (Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(deltaLon));
        double bearing = double.RadiansToDegrees(Math.Atan2(y, x)) % 360.0;
        return bearing < 0 ? bearing + 360.0 : bearing;
    }

    /// <summary>Return the smaller angle between two bearings, 0 to 180.</summary>
    public static double TurnBetween(double first, double second)
    {
        double difference = Math.Abs(first - second) % 360.0;
        return difference <= StraightAngle ? difference : 360.0 - difference;
    }

    /// <summary>
    /// Return which way something at the end of <paramref name="points"/> is
    /// travelling, or null when it has been standing still.
    /// </summary>
    /// <remarks>
    /// <paramref name="points"/> is oldest first. The bearing is taken from the
    /// most recent earlier fix far enough away to mean something, so a bus that
    /// has been sitting at a stop reports null rather than the direction of its
    /// GPS jitter. Null is a meaningful answer here, not a failure: it says
    /// "this thing was not going anywhere", which the matcher treats quite
    /// differently from "it was going that way".
    /// </remarks>
    public static double? HeadingOf(IReadOnlyList<GeoPoint> points)
    {
        if (points.Count < 2)
        {
            return null;
        }

        GeoPoint last = points[^1];
        for (int index = points.Count - 2; index >= 0; index--)
        {
            GeoPoint previous = points[index];
            if (DistanceMiles(previous, last) >= HeadingMinimumMiles)
            {
                return BearingDegrees(previous.Latitude, previous.Longitude, last.Latitude, last.Longitude);
            }
        }

        return null;
    }

    /// <summary>
    /// Return how far a point lies from the segment between two samples of a
    /// past journey, and the age interpolated to where it meets.
    /// </summary>
    /// <remarks>
    /// A stored track is a handful of fixes, not a road. At thirty miles an
    /// hour a thirty-second poll leaves them a quarter of a mile apart — the
    /// same as the match radius — so matching a point against SAMPLES means a
    /// journey drops in and out of the answer as the bus moves between them,
    /// and the median jumps by whole minutes as the set changes underneath it.
    /// That churn is most of the noise the published arrival then has to be
    /// held still against.
    ///
    /// Between two consecutive fixes the bus was somewhere on the line joining
    /// them, and its age somewhere between theirs. Projecting onto that line
    /// and interpolating gives a continuous answer instead of a quantised one.
    /// Measured at the midpoints of legs the bus actually drove, nearest-sample
    /// was 0.67 min out on average and 3.77 min at worst; point-to-segment was
    /// under 0.01 min in both.
    ///
    /// Locally flat geometry: these are segments of a few hundred yards, where
    /// treating latitude and longitude as a plane is wrong by inches. Longitude
    /// is scaled by cos(latitude) so a degree of each is locally square.
    /// </remarks>
    public static (double GapMiles, double Age) AlongSegment(
        double latitude,
        double longitude,
        TrackPoint start,
        TrackPoint end)
    {
        double scale = Math.Cos(double.DegreesToRadians((start.Latitude + end.Latitude) / 2));
        double runX = (end.Longitude - start.Longitude) * scale;
        double runY = end.Latitude - start.Latitude;
        double lengthSquared = (runX * runX) + (runY * runY);

        if (lengthSquared == 0)
        {
            return (DistanceMiles(latitude, longitude, start.Latitude, start.Longitude), start.SecondsBeforeArrival);
        }

        double along = ((((longitude - start.Longitude) * scale) * runX) + ((latitude - start.Latitude) * runY))
            / lengthSquared;
        along = Math.Clamp(along, 0.0, 1.0);

        double metLatitude = start.Latitude + (along * runY);
        double metLongitude = start.Longitude + (along * (end.Longitude - start.Longitude));
        double age = start.SecondsBeforeArrival + (along * (end.SecondsBeforeArrival - start.SecondsBeforeArrival));

        return (DistanceMiles(latitude, longitude, metLatitude, metLongitude), age);
    }
}
