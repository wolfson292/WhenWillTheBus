// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using System.Text.Json;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Prediction;

namespace WhenWillTheBus.Core.Storage;

/// <summary>
/// Writes learned history in the same bundle shape <see cref="HistoryImport"/>
/// reads.
/// </summary>
/// <remarks>
/// This exists so the phone can take history FROM the worker. They run the same
/// engine but keep separate copies, and only one of them is always on: the
/// worker watches every run, while the phone learns only while somebody has it
/// open. Without a way to copy across, the phone's estimates fall steadily
/// behind the worker's and nothing says so — they just get worse.
///
/// Deliberately the same format as the Home Assistant export, so there is one
/// importer rather than two.
///
/// PRIVACY: this carries real route coordinates — a child's daily journey to and
/// from school. It is served only to an authenticated caller and belongs on the
/// devices that already hold it, nowhere else.
/// </remarks>
public static class HistoryExport
{
    public static string ToBundle(IEnumerable<(long ChildId, IReadOnlyList<RunArrival> Arrivals)> riders)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("export_version", 1);
            writer.WriteString("exported_at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("source", "whenwillthebus worker");
            writer.WriteNumber("arrival_schema", 7);
            writer.WriteString("units", "miles");

            writer.WriteStartArray("anchor_ladder");
            foreach (double rung in Tuning.AnchorLadderMiles)
            {
                writer.WriteNumberValue(rung);
            }

            writer.WriteEndArray();

            writer.WriteStartArray("track_point_format");
            writer.WriteStringValue("seconds_before_arrival");
            writer.WriteStringValue("latitude");
            writer.WriteStringValue("longitude");
            writer.WriteEndArray();

            writer.WriteStartArray("riders");
            foreach ((long childId, IReadOnlyList<RunArrival> arrivals) in riders)
            {
                WriteRider(writer, childId, arrivals);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteRider(Utf8JsonWriter writer, long childId, IReadOnlyList<RunArrival> arrivals)
    {
        writer.WriteStartObject();

        // A string, because that is what the Home Assistant export emits and the
        // importer parses. Two shapes for one field would be a trap.
        writer.WriteString("child_id", childId.ToString(CultureInfo.InvariantCulture));

        writer.WriteStartArray("arrivals");
        foreach (RunArrival arrival in arrivals)
        {
            writer.WriteStartObject();
            writer.WriteString("run", arrival.Run == Run.Am ? "am" : "pm");
            writer.WriteString("arrival", arrival.Arrival.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteNumber("closest", Math.Round(arrival.ClosestMiles, 3));
            writer.WriteBoolean("substitute", arrival.Substitute);
            writer.WriteNumber("recedes", arrival.Recedes);
            writer.WriteNumber("stale", arrival.Stale);
            writer.WriteBoolean("replayed", arrival.Replayed);

            if (arrival.ErrorAtFiveMinutes is int five)
            {
                writer.WriteNumber("error_5min", five);
            }

            if (arrival.ErrorAtArrival is int final)
            {
                writer.WriteNumber("error_final", final);
            }

            if (arrival.Boarded is not null)
            {
                writer.WriteString("boarded", arrival.Boarded.Value.ToString("O", CultureInfo.InvariantCulture));
            }
            else
            {
                writer.WriteNull("boarded");
            }

            writer.WriteStartObject("legs");
            foreach ((int rung, int seconds) in arrival.Legs.OrderBy(leg => leg.Key))
            {
                writer.WriteNumber(rung.ToString(CultureInfo.InvariantCulture), seconds);
            }

            writer.WriteEndObject();

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
        writer.WriteEndObject();
    }
}
