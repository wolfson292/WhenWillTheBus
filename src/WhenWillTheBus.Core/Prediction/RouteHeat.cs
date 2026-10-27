// SPDX-License-Identifier: GPL-3.0-or-later

namespace WhenWillTheBus.Core.Prediction;

/// <summary>How often a stretch of road was driven, in words a parent reads.</summary>
public enum RouteHeatLevel
{
    Rarely = 0,
    Sometimes = 1,
    Often = 2,
    MostDays = 3,
}

/// <summary>One stretch of one past journey, and how common that stretch is.</summary>
public sealed record RouteHeatSegment(IReadOnlyList<GeoPoint> Path, RouteHeatLevel Level, int Journeys);

/// <summary>
/// Past journeys as a heatmap: every stretch coloured by how many journeys drove it.
/// </summary>
/// <remarks>
/// A STACK OF FAINT LINES ONLY WORKS FOR A HANDFUL. Translucent lines darken
/// where they coincide, but past about ten the common road saturates and a
/// one-off detour is still visible as a line of its own, so "usual" and "once"
/// read the same. Counting how many journeys passed each short stretch says it
/// directly.
///
/// Counted per JOURNEY, not per point. A bus dwelling at a stop leaves several
/// fixes in one place, and counting those would make every stop look like the
/// busiest road on the route.
///
/// The fixes are thirty seconds apart, so two journeys down the same road
/// almost never share a point. Each leg is filled in at a fraction of a cell,
/// and a stretch counts every journey that touched its cell or the ones beside
/// it, so a lane's width of GPS wander does not split one road into two.
/// </remarks>
public static class RouteHeat
{
    /// <summary>About 50 metres: finer than a block, coarser than GPS wander.</summary>
    public const double CellMiles = 0.03;

    /// <summary>A colour shorter than this cannot be seen, so it is not drawn as one.</summary>
    public const double MinimumStretchMiles = 0.1;

    /// <summary>Shorter than this, a straight leg cannot hide a turn worth drawing.</summary>
    private const double CornerLegMiles = 0.06;

    /// <summary>
    /// How far either side of a corner may be, against the straight leg it
    /// replaces. An L puts each side at 0.71 of its diagonal; the turn off
    /// S Tamiami Trail onto Pine Rd, at 125 degrees, at about 1.1.
    /// </summary>
    private const double CornerSlack = 1.25;

    /// <summary>Further than this from either fix, the corner is a guess, not a turn.</summary>
    private const double CornerReachMiles = 0.25;

    /// <summary>
    /// How much road on either side a corner needs before it can be put back.
    /// In the school car park the bus loops a few yards at a time, and carrying
    /// those "roads" forward drew spikes out into the field.
    /// </summary>
    private const double CornerHeadingMiles = 0.05;

    /// <summary>Other journeys that must have reported a fix at a corner before it is drawn.</summary>
    private const int CornerWitnesses = 2;

    /// <summary>A leg shorter than this is a bus creeping or waiting, and points nowhere.</summary>
    private const double BorrowedLegMiles = 0.02;

    /// <summary>How close another journey's leg must end or start to lend its direction.</summary>
    private const double BorrowedReachMiles = 0.03;

    /// <summary>Up to this long, a leg is short enough to say which way the road runs where it is.</summary>
    private const double ShortLegMiles = 0.15;

    /// <summary>Gentler than this is a bend in the road, which a straight line draws well enough.</summary>
    private const double CornerMinimumTurnDegrees = 30;

    /// <summary>Sharper than this is a U-turn, which has no single corner to put back.</summary>
    private const double CornerMaximumTurnDegrees = 150;

    /// <summary>Above this, the gap between two fixes is a lost signal, not a road.</summary>
    private const double GapMiles = 0.75;

    private const double MilesPerDegreeLatitude = 69.0;

    /// <summary>The level a share of all journeys falls into.</summary>
    public static RouteHeatLevel LevelFor(int journeys, int total)
    {
        if (total <= 0)
        {
            return RouteHeatLevel.Rarely;
        }

        double share = (double)journeys / total;
        return share switch
        {
            >= 0.75 => RouteHeatLevel.MostDays,
            >= 0.4 => RouteHeatLevel.Often,
            >= 0.15 => RouteHeatLevel.Sometimes,
            _ => RouteHeatLevel.Rarely,
        };
    }

    /// <summary>
    /// Split every journey into runs of one level, rarest first, so the common
    /// road is drawn last and lies on top where they overlap.
    /// </summary>
    public static IReadOnlyList<RouteHeatSegment> Build(IReadOnlyList<IReadOnlyList<GeoPoint>> routes)
    {
        List<IReadOnlyList<GeoPoint>> usable = [.. routes.Where(route => route.Count > 1)];
        if (usable.Count == 0)
        {
            return [];
        }

        // One longitude scale for the whole map: a school route spans a few
        // miles, over which cos(latitude) barely moves.
        double latitude = usable.SelectMany(route => route).Average(point => point.Latitude);
        double milesPerDegreeLongitude = MilesPerDegreeLatitude * Math.Max(Math.Cos(latitude * Math.PI / 180), 0.01);
        double latitudeStep = CellMiles / MilesPerDegreeLatitude;
        double longitudeStep = CellMiles / milesPerDegreeLongitude;

        // Before counting, so a corner cut through the buildings is neither
        // drawn nor counted as a road of its own.
        usable = FollowCorners(usable, milesPerDegreeLongitude);

        (long, long) CellOf(GeoPoint point) => (
            (long)Math.Floor(point.Latitude / latitudeStep),
            (long)Math.Floor(point.Longitude / longitudeStep));

        List<List<List<GeoPoint>>> filled = [.. usable.Select(Densify)];

        Dictionary<(long, long), HashSet<int>> visits = [];
        for (int journey = 0; journey < filled.Count; journey++)
        {
            foreach (GeoPoint point in filled[journey].SelectMany(leg => leg))
            {
                (long, long) cell = CellOf(point);
                if (!visits.TryGetValue(cell, out HashSet<int>? who))
                {
                    who = [];
                    visits[cell] = who;
                }

                who.Add(journey);
            }
        }

        int JourneysAt(GeoPoint point)
        {
            (long row, long column) = CellOf(point);
            HashSet<int> near = [];
            for (long dRow = -1; dRow <= 1; dRow++)
            {
                for (long dColumn = -1; dColumn <= 1; dColumn++)
                {
                    if (visits.TryGetValue((row + dRow, column + dColumn), out HashSet<int>? who))
                    {
                        near.UnionWith(who);
                    }
                }
            }

            return near.Count;
        }

        // The filler points exist to be counted, not drawn: they lie on the
        // straight line between two fixes, and drawing them only multiplied
        // the afternoon's map to twenty thousand points for the same picture.
        HashSet<GeoPoint> fixes = [.. usable.SelectMany(route => route)];
        IReadOnlyList<GeoPoint> Drawn(List<GeoPoint> run) =>
            [.. run.Where((point, index) => index == 0 || index == run.Count - 1 || fixes.Contains(point))];

        List<RouteHeatSegment> segments = [];
        foreach (List<GeoPoint> leg in filled.SelectMany(journey => journey))
        {
            List<(List<GeoPoint> Path, RouteHeatLevel Level, int Journeys)> runs = [];
            List<GeoPoint> run = [leg[0]];
            int count = JourneysAt(leg[0]);
            RouteHeatLevel level = LevelFor(count, filled.Count);

            for (int index = 1; index < leg.Count; index++)
            {
                int here = JourneysAt(leg[index]);
                RouteHeatLevel next = LevelFor(here, filled.Count);
                run.Add(leg[index]);

                if (next != level)
                {
                    runs.Add((run, level, count));

                    // Shares its first point with the last run's end, so the
                    // line has no gap where the colour changes.
                    run = [leg[index]];
                    level = next;
                    count = here;
                }
                else
                {
                    count = Math.Max(count, here);
                }
            }

            if (run.Count > 1)
            {
                runs.Add((run, level, count));
            }

            foreach ((List<GeoPoint> path, RouteHeatLevel kind, int journeys) in Smooth(runs))
            {
                segments.Add(new RouteHeatSegment(Drawn(path), kind, journeys));
            }
        }

        return [.. segments.OrderBy(segment => segment.Level)];
    }

    /// <summary>
    /// Put back the corner each journey cut between two fixes.
    /// </summary>
    /// <remarks>
    /// Fixes come thirty seconds apart, and a bus covers a fifth of a mile in
    /// that time, so a turn between two fixes is drawn as a straight line
    /// through whatever stands on the corner. Every journey cuts it at a
    /// different place, which on 26 Oct drew a fan of diagonals through the
    /// buildings at Pine Rd and S Tamiami Trail.
    ///
    /// The journey itself says where the corner was. It arrived along one road
    /// and left along another, so carrying the road it arrived on forward, and
    /// the one it left on back, meets at the turn. That point is used only when
    /// the heading really changed and the corner is somewhere the bus could
    /// have driven in the time: in front of the first fix, behind the second,
    /// and no further round than an L-shaped turn. Anything else, a U-turn in a
    /// car park or a curve, keeps its straight line, which is no worse than
    /// before.
    ///
    /// Taken from each journey's own fixes, never from the others'. Borrowing
    /// the other journeys' points for the bend mixed fixes from both sides of
    /// the corner and drew a road through the middle of the block.
    /// </remarks>
    private static List<IReadOnlyList<GeoPoint>> FollowCorners(
        List<IReadOnlyList<GeoPoint>> routes, double milesPerDegreeLongitude)
    {
        (double X, double Y) Plane(GeoPoint point) =>
            (point.Longitude * milesPerDegreeLongitude, point.Latitude * MilesPerDegreeLatitude);

        GeoPoint Back((double X, double Y) point) =>
            new(point.Y / MilesPerDegreeLatitude, point.X / milesPerDegreeLongitude);

        List<List<(double X, double Y)>> planes = [.. routes.Select(route => route.Select(Plane).ToList())];

        // Every short leg any journey drove, for the ends of a journey, where
        // its own fixes stop before they can say which way the road ran.
        List<(int Journey, (double X, double Y) From, (double X, double Y) To)> legs = [];
        for (int journey = 0; journey < planes.Count; journey++)
        {
            for (int index = 1; index < planes[journey].Count; index++)
            {
                double length = Distance(planes[journey][index - 1], planes[journey][index]);
                if (length is >= BorrowedLegMiles and <= ShortLegMiles)
                {
                    legs.Add((journey, planes[journey][index - 1], planes[journey][index]));
                }
            }
        }

        // The way the others drove INTO this point, or OUT of it.
        (double X, double Y)? Borrowed(int journey, (double X, double Y) at, bool into)
        {
            double x = 0;
            double y = 0;
            int count = 0;
            foreach ((int other, (double X, double Y) from, (double X, double Y) to) in legs)
            {
                if (other != journey && Distance(into ? to : from, at) < BorrowedReachMiles)
                {
                    (double ux, double uy) = Unit(to.X - from.X, to.Y - from.Y);
                    x += ux;
                    y += uy;
                    count++;
                }
            }

            // Two journeys at least, and broadly agreeing: an average of roads
            // that point every which way is not a direction.
            return count >= 2 && Math.Sqrt((x * x) + (y * y)) / count >= 0.7 ? Unit(x, y) : null;
        }

        // HOW MANY OTHER JOURNEYS REPORTED A FIX THERE. A real corner is a place
        // buses actually are: on 26 Oct about a dozen had a fix within yards
        // of the Pine Rd turn. Lines carried forward around the school car
        // park met out in the field, where no bus has ever reported, and drew
        // spikes there.
        int Witnesses(int journey, (double X, double Y) corner)
        {
            HashSet<int> seen = [];
            for (int other = 0; other < planes.Count; other++)
            {
                if (other != journey && planes[other].Any(point => Distance(point, corner) < BorrowedReachMiles))
                {
                    seen.Add(other);
                }
            }

            return seen.Count;
        }

        List<IReadOnlyList<GeoPoint>> bent = [];
        for (int journey = 0; journey < routes.Count; journey++)
        {
            List<(double X, double Y)> plane = planes[journey];
            List<GeoPoint> path = [routes[journey][0]];

            for (int index = 1; index < plane.Count; index++)
            {
                if (Corner(plane, index - 1, index, at => Borrowed(journey, at, into: true), at => Borrowed(journey, at, into: false))
                    is (double, double) corner
                    && Witnesses(journey, corner) >= CornerWitnesses)
                {
                    path.Add(Back(corner));
                }

                path.Add(routes[journey][index]);
            }

            bent.Add(path);
        }

        return bent;
    }

    /// <summary>Where the road into fix <paramref name="from"/> meets the road out of fix <paramref name="to"/>, if they turn.</summary>
    private static (double X, double Y)? Corner(
        List<(double X, double Y)> plane,
        int from,
        int to,
        Func<(double X, double Y), (double X, double Y)?> arrivingAt,
        Func<(double X, double Y), (double X, double Y)?> leavingFrom)
    {
        (double ax, double ay) = plane[from];
        (double bx, double by) = plane[to];
        double chord = Distance(plane[from], plane[to]);

        if (chord < CornerLegMiles || chord > GapMiles)
        {
            return null;
        }

        // The road it came in on, read far enough back to be a direction
        // rather than GPS wander -- a bus waiting at a light repeats its fix.
        int before = from - 1;
        while (before >= 0 && Distance(plane[before], plane[from]) < CornerHeadingMiles)
        {
            before--;
        }

        int after = to + 1;
        while (after < plane.Count && Distance(plane[to], plane[after]) < CornerHeadingMiles)
        {
            after++;
        }

        // Unit directions: arriving at A, and leaving B. Where the journey has
        // no fix beyond the leg -- the morning track ENDS at the stop, which
        // is exactly where the fan was -- the other journeys say which way
        // the road runs there.
        (double X, double Y)? arriving = before >= 0
            ? Unit(ax - plane[before].X, ay - plane[before].Y)
            : leavingFrom(plane[from]);
        (double X, double Y)? leaving = after < plane.Count
            ? Unit(plane[after].X - bx, plane[after].Y - by)
            : arrivingAt(plane[to]);

        if (arriving is not (double inX, double inY) || leaving is not (double outX, double outY))
        {
            return null;
        }

        double turn = Math.Acos(Math.Clamp((inX * outX) + (inY * outY), -1, 1)) * 180 / Math.PI;
        if (turn is < CornerMinimumTurnDegrees or > CornerMaximumTurnDegrees)
        {
            return null;
        }

        // A + s*in = B - t*out, solved for s and t.
        double cross = (inX * -outY) - (inY * -outX);
        if (Math.Abs(cross) < 1e-9)
        {
            return null;
        }

        double rx = bx - ax;
        double ry = by - ay;
        double s = ((rx * -outY) - (ry * -outX)) / cross;
        double t = ((inY * rx) - (inX * ry)) / cross;

        if (s <= 0 || t <= 0)
        {
            return null;
        }

        (double X, double Y) corner = (ax + (s * inX), ay + (s * inY));
        return Math.Max(s, t) <= Math.Min(chord * CornerSlack, CornerReachMiles) ? corner : null;
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b) =>
        Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    private static (double X, double Y) Unit(double x, double y)
    {
        double length = Math.Sqrt((x * x) + (y * y));
        return length == 0 ? (0, 0) : (x / length, y / length);
    }

    /// <summary>
    /// Fold every stretch too short to see into the one before it.
    /// </summary>
    /// <remarks>
    /// Where a road crosses a cell's edge or meets a side street, the count
    /// flickers over a few metres, and each flicker was a separate line on the
    /// map. Nobody can see a colour 30 metres long, but MapKit still has to
    /// draw it: on 26 Oct those fragments were most of the 175 overlays that
    /// held the main thread until iOS killed the app.
    /// </remarks>
    private static List<(List<GeoPoint> Path, RouteHeatLevel Level, int Journeys)> Smooth(
        List<(List<GeoPoint> Path, RouteHeatLevel Level, int Journeys)> runs)
    {
        List<(List<GeoPoint> Path, RouteHeatLevel Level, int Journeys)> kept = [];

        foreach ((List<GeoPoint> Path, RouteHeatLevel Level, int Journeys) run in runs)
        {
            if (kept.Count > 0
                && (kept[^1].Level == run.Level || Length(run.Path) < MinimumStretchMiles || Length(kept[^1].Path) < MinimumStretchMiles))
            {
                // The longer of the two decides the colour.
                (List<GeoPoint> Path, RouteHeatLevel Level, int Journeys) last = kept[^1];
                bool lastWins = Length(last.Path) >= Length(run.Path);
                last.Path.AddRange(run.Path.Skip(1));
                kept[^1] = lastWins ? last : (last.Path, run.Level, run.Journeys);
                continue;
            }

            kept.Add(run);
        }

        return kept;
    }

    private static double Length(List<GeoPoint> path)
    {
        double miles = 0;
        for (int index = 1; index < path.Count; index++)
        {
            miles += Geo.DistanceMiles(path[index - 1], path[index]);
        }

        return miles;
    }

    /// <summary>
    /// A journey with a point every half cell, broken wherever the signal was lost.
    /// </summary>
    private static List<List<GeoPoint>> Densify(IReadOnlyList<GeoPoint> route)
    {
        List<List<GeoPoint>> legs = [];
        List<GeoPoint> current = [route[0]];

        for (int index = 1; index < route.Count; index++)
        {
            GeoPoint from = route[index - 1];
            GeoPoint to = route[index];
            double miles = Geo.DistanceMiles(from, to);

            // A straight line across a lost signal is a road nobody drove.
            if (miles > GapMiles)
            {
                if (current.Count > 1)
                {
                    legs.Add(current);
                }

                current = [to];
                continue;
            }

            int steps = Math.Max(1, (int)Math.Ceiling(miles / (CellMiles / 2)));
            for (int step = 1; step < steps; step++)
            {
                double along = (double)step / steps;
                current.Add(new GeoPoint(
                    from.Latitude + ((to.Latitude - from.Latitude) * along),
                    from.Longitude + ((to.Longitude - from.Longitude) * along)));
            }

            // The fix itself, exactly, so drawing can tell it from the filler.
            current.Add(to);
        }

        if (current.Count > 1)
        {
            legs.Add(current);
        }

        return legs;
    }
}
