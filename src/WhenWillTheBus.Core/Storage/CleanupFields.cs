// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using WhenWillTheBus.Core.Api;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Prediction;

namespace WhenWillTheBus.Core.Storage;

/// <summary>
/// The fields the worker adds to a run after the fact: the road it was matched
/// to, and the review of what it was.
/// </summary>
/// <remarks>
/// One reader and one writer, shared by the history file and the bundle the
/// phone takes, so the two cannot drift. Every field is optional: a file or a
/// bundle written before cleanup existed reads as a run not yet cleaned.
/// </remarks>
public static class CleanupFields
{
    public static void Write(Utf8JsonWriter writer, RunArrival arrival)
    {
        if (arrival.RoadConfidence is double confidence)
        {
            writer.WriteNumber("road_confidence", Math.Round(confidence, 3));

            // Latitude, longitude: no timing, because the road is for drawing.
            writer.WriteStartArray("road");
            foreach (GeoPoint point in arrival.Road)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(Math.Round(point.Latitude, Tuning.CoordinatePrecision));
                writer.WriteNumberValue(Math.Round(point.Longitude, Tuning.CoordinatePrecision));
                writer.WriteEndArray();
            }

            writer.WriteEndArray();
        }

        if (arrival.Review is RunReview review)
        {
            writer.WriteStartObject("review");
            writer.WriteString("verdict", Name(review.Verdict));
            writer.WriteString("note", review.Note);
            writer.WriteEndObject();
        }
    }

    /// <summary>Read the fields back onto a run that has everything else.</summary>
    public static RunArrival Read(JsonElement item, RunArrival arrival)
    {
        List<GeoPoint> road = [];
        foreach (JsonElement point in item.Array("road"))
        {
            if (point.ValueKind == JsonValueKind.Array && point.GetArrayLength() >= 2)
            {
                JsonElement[] parts = [.. point.EnumerateArray()];
                road.Add(new GeoPoint(parts[0].GetDouble(), parts[1].GetDouble()));
            }
        }

        RunReview? review = null;
        if (item.Property("review") is { ValueKind: JsonValueKind.Object } source
            && Parse(source.String("verdict")) is RunVerdict verdict)
        {
            review = new RunReview(verdict, source.String("note") ?? string.Empty);
        }

        return arrival with
        {
            Road = road,
            RoadConfidence = item.Double("road_confidence"),
            Review = review,
        };
    }

    public static string Name(RunVerdict verdict) => verdict switch
    {
        RunVerdict.Detour => "detour",
        RunVerdict.Substitute => "substitute",
        RunVerdict.BadGps => "bad_gps",
        _ => "usual",
    };

    public static RunVerdict? Parse(string? name) => name switch
    {
        "usual" => RunVerdict.Usual,
        "detour" => RunVerdict.Detour,
        "substitute" => RunVerdict.Substitute,
        "bad_gps" => RunVerdict.BadGps,
        _ => null,
    };
}
