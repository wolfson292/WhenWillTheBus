// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using WhenWillTheBus.Core.Model;

namespace WhenWillTheBus.Core.Prediction;

/// <summary>
/// One run described in numbers, against the runs before it -- with no
/// coordinates in it.
/// </summary>
/// <remarks>
/// This is what leaves the house when a run is reviewed. A child's route is a
/// list of places; this is a list of facts about a journey -- how long, how
/// far, how much of it on the usual roads, how healthy the GPS was -- from
/// which nobody could draw the route or find the stop.
/// </remarks>
public sealed record RunSummary
{
    /// <summary>Within this of another run's road, a stretch counts as the usual road.</summary>
    public const double UsualRoadMiles = 0.05;

    public required Run Run { get; init; }
    public required DateOnly Date { get; init; }
    public required TimeOnly Arrived { get; init; }
    public TimeOnly? UsualArrival { get; init; }
    public required int Fixes { get; init; }
    public required int LongestGapSeconds { get; init; }
    public required int Stale { get; init; }
    public required int Recedes { get; init; }
    public required bool SubstituteBus { get; init; }
    public double? MatchConfidence { get; init; }
    public double? Miles { get; init; }
    public double? UsualMiles { get; init; }

    /// <summary>Share of this run's road within <see cref="UsualRoadMiles"/> of another run's.</summary>
    public double? OnUsualRoads { get; init; }

    public required int Compared { get; init; }

    /// <summary>Summarise <paramref name="arrival"/> against the other runs of the same kind.</summary>
    public static RunSummary Of(RunArrival arrival, IEnumerable<RunArrival> others, LocalClock clock)
    {
        List<RunArrival> peers = [.. others.Where(other => other.Run == arrival.Run && other.Arrival != arrival.Arrival)];

        DateTime local = clock.ToLocal(arrival.Arrival);
        List<int> usualMinutes = [.. peers
            .Select(peer => clock.ToLocal(peer.Arrival))
            .Select(time => (time.Hour * 60) + time.Minute)
            .Order()];

        int gap = 0;
        for (int index = 1; index < arrival.Track.Count; index++)
        {
            gap = Math.Max(gap, Math.Abs(arrival.Track[index - 1].SecondsBeforeArrival - arrival.Track[index].SecondsBeforeArrival));
        }

        IReadOnlyList<GeoPoint> road = Drawn(arrival);
        List<double> usualMiles = [.. peers.Select(Drawn).Where(line => line.Count > 1).Select(Length).Order()];

        return new RunSummary
        {
            Run = arrival.Run,
            Date = DateOnly.FromDateTime(local),
            Arrived = TimeOnly.FromDateTime(local),
            UsualArrival = usualMinutes.Count == 0
                ? null
                : new TimeOnly(Statistics.Median(usualMinutes) / 60, Statistics.Median(usualMinutes) % 60),
            Fixes = arrival.Track.Count,
            LongestGapSeconds = gap,
            Stale = arrival.Stale,
            Recedes = arrival.Recedes,
            SubstituteBus = arrival.Substitute,
            MatchConfidence = arrival.RoadConfidence,
            Miles = road.Count > 1 ? Length(road) : null,
            UsualMiles = usualMiles.Count == 0 ? null : usualMiles[usualMiles.Count / 2],
            OnUsualRoads = road.Count > 1 && peers.Count > 0 ? Shared(road, peers.Select(Drawn)) : null,
            Compared = peers.Count,
        };
    }

    /// <summary>The summary as the reviewer reads it.</summary>
    public string Describe()
    {
        StringBuilder text = new();
        void Line(string label, string? value)
        {
            if (value is not null)
            {
                text.Append(label).Append(": ").AppendLine(value);
            }
        }

        CultureInfo culture = CultureInfo.InvariantCulture;
        Line("Run", Run == Run.Am ? "morning, home stop to school" : "afternoon, school to home stop");
        Line("Day", Date.ToString("dddd d MMMM yyyy", culture));
        Line("Reached the stop", Arrived.ToString("h:mm tt", culture));
        Line("Usual time at the stop", UsualArrival?.ToString("h:mm tt", culture));
        Line("Distance driven, as matched to roads", Miles?.ToString("F1", culture) + (Miles is null ? null : " miles"));
        Line("Usual distance for this run", UsualMiles?.ToString("F1", culture) + (UsualMiles is null ? null : " miles"));
        Line("Share of the route on roads other runs used", OnUsualRoads is double share ? $"{share:P0}" : null);
        Line("Road-match confidence (0 to 1)", MatchConfidence?.ToString("F2", culture));
        Line("Live GPS positions that make up the route", Fixes.ToString(culture));
        Line("Longest gap between live positions", $"{LongestGapSeconds} seconds");

        // NOT a share of the positions above. These are polls that found no
        // fresh position at all and were left out of the route; on 14 Oct the
        // review read "61 stale out of 22" as a broken recording, of a run that
        // matched the roads at 98%.
        Line("Polls with no live position, left out of the route (normal while a bus is parked or before it sets off)", Stale.ToString(culture));
        Line("Times the bus got closer to the stop and then further away again", Recedes.ToString(culture));
        Line("Reported as a substitute bus", SubstituteBus ? "yes" : "no");
        Line("Other runs of this kind compared against", Compared.ToString(culture));
        return text.ToString();
    }

    private static IReadOnlyList<GeoPoint> Drawn(RunArrival arrival) =>
        arrival.Road.Count > 1
            ? arrival.Road
            : [.. arrival.Track.Select(point => new GeoPoint(point.Latitude, point.Longitude))];

    private static double Length(IReadOnlyList<GeoPoint> line)
    {
        double miles = 0;
        for (int index = 1; index < line.Count; index++)
        {
            miles += Geo.DistanceMiles(line[index - 1], line[index]);
        }

        return miles;
    }

    /// <summary>The share of <paramref name="road"/>, by length, that runs alongside any of <paramref name="others"/>.</summary>
    private static double Shared(IReadOnlyList<GeoPoint> road, IEnumerable<IReadOnlyList<GeoPoint>> others)
    {
        List<GeoPoint> mine = Along(road);
        if (mine.Count == 0)
        {
            return 0;
        }

        // Bucketed by cells the size of the tolerance, so each point is only
        // compared with what is in its own cell and the eight around it: a
        // whole term of runs is otherwise millions of distance sums a run.
        double latitudeStep = UsualRoadMiles / 69.0;
        double longitudeStep = UsualRoadMiles / (69.0 * Math.Max(Math.Cos(road[0].Latitude * Math.PI / 180), 0.01));
        (long, long) CellOf(GeoPoint point) => (
            (long)Math.Floor(point.Latitude / latitudeStep),
            (long)Math.Floor(point.Longitude / longitudeStep));

        Dictionary<(long, long), List<GeoPoint>> cells = [];
        foreach (GeoPoint point in others.SelectMany(Along))
        {
            (long, long) cell = CellOf(point);
            if (!cells.TryGetValue(cell, out List<GeoPoint>? bucket))
            {
                bucket = [];
                cells[cell] = bucket;
            }

            bucket.Add(point);
        }

        bool Near(GeoPoint point)
        {
            (long row, long column) = CellOf(point);
            for (long dRow = -1; dRow <= 1; dRow++)
            {
                for (long dColumn = -1; dColumn <= 1; dColumn++)
                {
                    if (cells.TryGetValue((row + dRow, column + dColumn), out List<GeoPoint>? bucket)
                        && bucket.Any(other => Geo.DistanceMiles(point, other) <= UsualRoadMiles))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        return (double)mine.Count(Near) / mine.Count;
    }

    /// <summary>A point every hundredth of a mile along a line, so long legs count for their length.</summary>
    private static List<GeoPoint> Along(IReadOnlyList<GeoPoint> line)
    {
        const double step = 0.01;
        List<GeoPoint> points = [];
        for (int index = 1; index < line.Count; index++)
        {
            GeoPoint from = line[index - 1];
            GeoPoint to = line[index];
            int steps = Math.Max(1, (int)Math.Ceiling(Geo.DistanceMiles(from, to) / step));
            for (int part = 0; part < steps; part++)
            {
                double along = (double)part / steps;
                points.Add(new GeoPoint(
                    from.Latitude + ((to.Latitude - from.Latitude) * along),
                    from.Longitude + ((to.Longitude - from.Longitude) * along)));
            }
        }

        return points;
    }
}
