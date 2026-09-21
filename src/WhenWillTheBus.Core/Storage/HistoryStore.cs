// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.Json;
using WhenWillTheBus.Core.Api;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Prediction;

namespace WhenWillTheBus.Core.Storage;

/// <summary>Everything worth keeping between launches, for one rider.</summary>
public sealed record StoredRider
{
    public required long ChildId { get; init; }

    public IReadOnlyList<RunArrival> Arrivals { get; init; } = [];

    /// <summary>
    /// Accumulated locally, because <c>getStudentScan</c> ONLY EVER RETURNS THE
    /// CURRENT DAY. Without this, all scan history is lost at midnight.
    /// </summary>
    public IReadOnlyList<ScanEvent> Scans { get; init; } = [];
}

/// <summary>
/// Reads and writes the learned history as JSON.
/// </summary>
/// <remarks>
/// Written by hand with <see cref="Utf8JsonWriter"/> rather than a serializer,
/// so it survives the trimming and AOT compilation an iOS build applies without
/// needing reflection metadata kept alive.
///
/// PRIVACY: this file holds a child's route. On iOS it belongs in the app
/// container (and is excluded from iCloud backup if you would rather it never
/// left the device); on a server it belongs on a volume you control. It is never
/// something to attach to a bug report.
/// </remarks>
public static class HistoryStore
{
    /// <summary>Bumped when the shape changes in a way a reader must notice.</summary>
    public const int Version = 1;

    /// <summary>
    /// Bounded by count rather than age: it keeps storage small without the
    /// retained history depending on how long the app has been running.
    /// </summary>
    public const int ScanHistoryLimit = 50;

    public static string Serialise(IEnumerable<StoredRider> riders)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", Version);
            writer.WriteString("units", "miles");
            writer.WriteStartArray("riders");

            foreach (StoredRider rider in riders)
            {
                WriteRider(writer, rider);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public static IReadOnlyList<StoredRider> Deserialise(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        // An unknown future version is not readable, and guessing at it would
        // quietly learn from a shape that means something else.
        long version = root.Long("version") ?? 0;
        if (version > Version)
        {
            throw new InvalidDataException(
                $"History was written by a newer version ({version} > {Version}).");
        }

        List<StoredRider> riders = [];
        foreach (JsonElement rider in root.Array("riders"))
        {
            long? childId = rider.Long("child_id");
            if (childId is null)
            {
                continue;
            }

            riders.Add(new StoredRider
            {
                ChildId = childId.Value,
                Arrivals = ReadArrivals(rider),
                Scans = ReadScans(rider),
            });
        }

        return riders;
    }

    public static async Task SaveAsync(string path, IEnumerable<StoredRider> riders, CancellationToken token = default)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write beside the target and move into place, so a crash or a killed
        // app cannot leave a half-written history behind. Losing a fortnight of
        // learned journeys to a torn write would cost six weeks to rebuild.
        string temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, Serialise(riders), token).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    public static async Task<IReadOnlyList<StoredRider>> LoadAsync(string path, CancellationToken token = default)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        return Deserialise(await File.ReadAllTextAsync(path, token).ConfigureAwait(false));
    }

    private static void WriteRider(Utf8JsonWriter writer, StoredRider rider)
    {
        writer.WriteStartObject();
        writer.WriteNumber("child_id", rider.ChildId);

        writer.WriteStartArray("arrivals");
        foreach (RunArrival arrival in rider.Arrivals)
        {
            writer.WriteStartObject();
            writer.WriteString("run", arrival.Run == Run.Am ? "am" : "pm");
            writer.WriteString("arrival", arrival.Arrival.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteNumber("closest", Math.Round(arrival.ClosestMiles, 3));
            writer.WriteBoolean("substitute", arrival.Substitute);
            writer.WriteNumber("recedes", arrival.Recedes);
            writer.WriteNumber("stale", arrival.Stale);
            writer.WriteBoolean("replayed", arrival.Replayed);

            if (arrival.Boarded is not null)
            {
                writer.WriteString("boarded", arrival.Boarded.Value.ToString("O", CultureInfo.InvariantCulture));
            }

            writer.WriteStartObject("legs");
            foreach ((int rung, int seconds) in arrival.Legs.OrderBy(leg => leg.Key))
            {
                writer.WriteNumber(rung.ToString(CultureInfo.InvariantCulture), seconds);
            }

            writer.WriteEndObject();

            // Five decimal places, a little over a metre. Seven would triple the
            // size of the store to record GPS noise.
            writer.WriteStartArray("track");
            foreach (TrackPoint point in arrival.Track)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(point.SecondsBeforeArrival);
                writer.WriteNumberValue(Math.Round(point.Latitude, Tuning.CoordinatePrecision));
                writer.WriteNumberValue(Math.Round(point.Longitude, Tuning.CoordinatePrecision));
                writer.WriteEndArray();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("scans");
        foreach (ScanEvent scan in rider.Scans.TakeLast(ScanHistoryLimit))
        {
            writer.WriteStartObject();
            writer.WriteString("at", scan.Timestamp.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("kind", scan.Kind == ScanKind.Pickup ? "pickup" : "dropoff");

            if (scan.Location is not null)
            {
                writer.WriteString("location", scan.Location);
            }

            if (scan.Method is not null)
            {
                writer.WriteString("method", scan.Method);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static List<RunArrival> ReadArrivals(JsonElement rider)
    {
        List<RunArrival> arrivals = [];

        foreach (JsonElement item in rider.Array("arrivals"))
        {
            if (!TryInstant(item.String("arrival"), out DateTimeOffset when))
            {
                continue;
            }

            Dictionary<int, int> legs = [];
            if (item.Property("legs") is { ValueKind: JsonValueKind.Object } source)
            {
                foreach (JsonProperty leg in source.EnumerateObject())
                {
                    if (int.TryParse(leg.Name, CultureInfo.InvariantCulture, out int rung)
                        && leg.Value.ValueKind == JsonValueKind.Number)
                    {
                        legs[rung] = leg.Value.GetInt32();
                    }
                }
            }

            List<TrackPoint> track = [];
            foreach (JsonElement point in item.Array("track"))
            {
                if (point.ValueKind == JsonValueKind.Array && point.GetArrayLength() >= 3)
                {
                    JsonElement[] parts = point.EnumerateArray().ToArray();
                    track.Add(new TrackPoint(parts[0].GetInt32(), parts[1].GetDouble(), parts[2].GetDouble()));
                }
            }

            arrivals.Add(new RunArrival
            {
                Run = string.Equals(item.String("run"), "pm", StringComparison.OrdinalIgnoreCase) ? Run.Pm : Run.Am,
                Arrival = when,
                ClosestMiles = item.Double("closest") ?? 0,
                Substitute = item.Bool("substitute"),
                Legs = legs,
                Track = track,
                Recedes = (int)(item.Long("recedes") ?? 0),
                Stale = (int)(item.Long("stale") ?? 0),
                Replayed = item.Bool("replayed"),
                Boarded = TryInstant(item.String("boarded"), out DateTimeOffset boarded) ? boarded : null,
            });
        }

        return arrivals;
    }

    private static List<ScanEvent> ReadScans(JsonElement rider)
    {
        List<ScanEvent> scans = [];

        foreach (JsonElement item in rider.Array("scans"))
        {
            if (!TryInstant(item.String("at"), out DateTimeOffset when))
            {
                continue;
            }

            scans.Add(new ScanEvent(
                when,
                item.String("location"),
                string.Equals(item.String("kind"), "dropoff", StringComparison.OrdinalIgnoreCase)
                    ? ScanKind.Dropoff
                    : ScanKind.Pickup,
                item.String("method")));
        }

        return scans;
    }

    private static bool TryInstant(string? value, out DateTimeOffset instant)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            instant = default;
            return false;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out instant);
    }
}
