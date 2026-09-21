// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.Json;
using WhenWillTheBus.Core.Api;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Prediction;

namespace WhenWillTheBus.Core.Storage;

/// <summary>One rider's history, as it came out of the export bundle.</summary>
public sealed record ImportedRider(string ChildId, IReadOnlyList<RunArrival> Arrivals);

/// <summary>
/// Reads the history bundle exported from the Home Assistant integration.
/// </summary>
/// <remarks>
/// PRIVACY. This file contains real route coordinates. A few miles of turns can
/// be matched against a road network and put back on the map, and it is a
/// child's daily route to and from school. Keep it on-device and local: never
/// commit it, never attach it to a bug report, never upload it to a service.
/// Nothing in this class logs a coordinate.
///
/// Predictions work from TWO route samples and three arrivals for outlier
/// rejection, so a fortnight of this makes the app useful immediately.
/// </remarks>
public static class HistoryImport
{
    private const int TrackPointLength = 3;

    /// <summary>Parse an export bundle.</summary>
    /// <param name="json">The bundle's contents.</param>
    /// <exception cref="InvalidDataException">The bundle is not one.</exception>
    public static IReadOnlyList<ImportedRider> Parse(string json)
    {
        using JsonDocument document = Parse(json, out JsonElement root);

        if (root.Property("riders") is null)
        {
            throw new InvalidDataException("Not a WheresTheBus history export: no riders.");
        }

        // The bundle records the units it was written in. The reference
        // integration carried parallel mile and kilometre constants, so a
        // kilometre account's export is in kilometres; everything here works in
        // miles.
        bool inKilometres = string.Equals(root.String("units"), "km", StringComparison.OrdinalIgnoreCase);

        // Leg keys are INDICES INTO THE EXPORT'S OWN LADDER, not into ours. If
        // the two ladders ever differ, trusting the index silently relabels a
        // three-mile leg as a two-mile one, and every anchored estimate built on
        // it is wrong by the difference. So the rungs are remapped by DISTANCE.
        double[] exportedLadder = ReadLadder(root, inKilometres);

        List<ImportedRider> riders = [];
        foreach (JsonElement rider in root.Array("riders"))
        {
            string? childId = rider.String("child_id");
            if (string.IsNullOrEmpty(childId))
            {
                continue;
            }

            List<RunArrival> arrivals = [];
            foreach (JsonElement arrival in rider.Array("arrivals"))
            {
                RunArrival? parsed = ReadArrival(arrival, exportedLadder, inKilometres);
                if (parsed is not null)
                {
                    arrivals.Add(parsed);
                }
            }

            arrivals.Sort((left, right) => left.Arrival.CompareTo(right.Arrival));
            riders.Add(new ImportedRider(childId, arrivals));
        }

        return riders;
    }

    private static JsonDocument Parse(string json, out JsonElement root)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("History export is not valid JSON.", error);
        }

        root = document.RootElement;
        return document;
    }

    private static double[] ReadLadder(JsonElement root, bool inKilometres)
    {
        IReadOnlyList<JsonElement> rungs = root.Array("anchor_ladder");
        if (rungs.Count == 0)
        {
            return Tuning.AnchorLadderMiles;
        }

        return [.. rungs
            .Where(rung => rung.ValueKind == JsonValueKind.Number)
            .Select(rung => inKilometres ? Geo.KilometresToMiles(rung.GetDouble()) : rung.GetDouble())];
    }

    private static RunArrival? ReadArrival(JsonElement arrival, double[] exportedLadder, bool inKilometres)
    {
        if (!TryReadInstant(arrival.String("arrival"), out DateTimeOffset when))
        {
            return null;
        }

        Run run = string.Equals(arrival.String("run"), "pm", StringComparison.OrdinalIgnoreCase)
            ? Run.Pm
            : Run.Am;

        double closest = arrival.Double("closest") ?? 0.0;

        return new RunArrival
        {
            Run = run,
            Arrival = when,
            ClosestMiles = inKilometres ? Geo.KilometresToMiles(closest) : closest,
            Substitute = arrival.Bool("substitute"),
            Legs = ReadLegs(arrival, exportedLadder),
            Track = ReadTrack(arrival),
            Recedes = (int)(arrival.Long("recedes") ?? 0),
            Stale = (int)(arrival.Long("stale") ?? 0),
            Boarded = TryReadInstant(arrival.String("boarded"), out DateTimeOffset boarded) ? boarded : null,

            // Whether history has been replayed for this arrival is a property of
            // the arrival, and it survives the import so a later backfill knows
            // this one has already been read.
            Replayed = arrival.Bool("replayed"),

            // Absent from a Home Assistant export, which never recorded it.
            ErrorAtFiveMinutes = (int?)arrival.Long("error_5min"),
            ErrorAtArrival = (int?)arrival.Long("error_final"),
        };
    }

    /// <summary>
    /// How far a rung may be from one of ours and still be the same rung.
    /// </summary>
    /// <remarks>
    /// Five per cent, RELATIVE, because a kilometre ladder is a rounded
    /// approximation of a mile one: the reference integration's [4.8, 3.2, 1.6,
    /// 0.8] km converts to [2.98, 1.99, 0.99, 0.50] miles, which is plainly the
    /// same ladder and is not within any tolerance tight enough to be absolute.
    /// Relative also keeps the 0.5 mi rung from swallowing anything, where a
    /// fixed tolerance generous enough for 3.0 would.
    /// </remarks>
    private const double RungTolerance = 0.05;

    /// <summary>Remap each leg from the export's ladder onto ours, by distance.</summary>
    private static Dictionary<int, int> ReadLegs(JsonElement arrival, double[] exportedLadder)
    {
        Dictionary<int, int> legs = [];
        Dictionary<int, double> bestGap = [];

        JsonElement? source = arrival.Property("legs");
        if (source?.ValueKind != JsonValueKind.Object)
        {
            return legs;
        }

        foreach (JsonProperty leg in source.Value.EnumerateObject())
        {
            if (!int.TryParse(leg.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int exportedIndex)
                || exportedIndex < 0
                || exportedIndex >= exportedLadder.Length
                || leg.Value.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            double rungMiles = exportedLadder[exportedIndex];
            (int ours, double gap) = NearestRung(rungMiles);

            // A rung this build does not have is dropped rather than guessed at.
            // The arrival keeps its track, which is what the route basis needs.
            if (ours < 0)
            {
                continue;
            }

            // Two exported rungs can both land nearest the same one of ours; the
            // closer match wins rather than whichever happened to be read last.
            if (!bestGap.TryGetValue(ours, out double previous) || gap < previous)
            {
                bestGap[ours] = gap;
                legs[ours] = leg.Value.GetInt32();
            }
        }

        return legs;
    }

    /// <summary>The rung of our ladder a distance belongs to, or -1 if none does.</summary>
    private static (int Index, double Gap) NearestRung(double miles)
    {
        int best = -1;
        double bestGap = double.MaxValue;

        for (int index = 0; index < Tuning.AnchorLadderMiles.Length; index++)
        {
            double gap = Math.Abs(Tuning.AnchorLadderMiles[index] - miles);
            if (gap <= Tuning.AnchorLadderMiles[index] * RungTolerance && gap < bestGap)
            {
                best = index;
                bestGap = gap;
            }
        }

        return (best, bestGap);
    }

    /// <summary>Read the route track — the most valuable field in the bundle.</summary>
    private static List<TrackPoint> ReadTrack(JsonElement arrival)
    {
        List<TrackPoint> track = [];

        foreach (JsonElement point in arrival.Array("track"))
        {
            if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() < TrackPointLength)
            {
                continue;
            }

            JsonElement[] parts = point.EnumerateArray().ToArray();
            if (parts.Any(part => part.ValueKind != JsonValueKind.Number))
            {
                continue;
            }

            track.Add(new TrackPoint(parts[0].GetInt32(), parts[1].GetDouble(), parts[2].GetDouble()));
        }

        // Oldest first, which is what the matcher's segment walk assumes.
        track.Sort((left, right) => right.SecondsBeforeArrival.CompareTo(left.SecondsBeforeArrival));
        return track;
    }

    private static bool TryReadInstant(string? value, out DateTimeOffset instant)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            instant = default;
            return false;
        }

        return DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out instant);
    }
}
