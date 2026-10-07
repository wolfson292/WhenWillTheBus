// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using System.Text.Json;
using WhenWillTheBus.Core.Model;

namespace WhenWillTheBus.Core.Notifications;

/// <summary>
/// What the home-screen widget shows, as the JSON it reads.
/// </summary>
/// <remarks>
/// Built HERE so the app and the worker produce the same bytes. The app writes
/// it into the App Group after every poll; the worker serves it at /widget for
/// a widget whose app has not run for hours -- which on a school day is most
/// of them. On 7 Oct the widget had nothing to say all afternoon while the
/// worker knew exactly where the bus was.
///
/// EVERY FIELD HERE MUST MATCH <c>BusSnapshot</c> in
/// ios/LiveActivity/BusSnapshot.swift. Two processes, two languages, and a
/// mismatch is a widget stuck on its placeholder with nothing explaining why.
/// </remarks>
public static class HomeSnapshot
{
    public static string Serialise(
        string? riderName,
        string? busNumber,
        Journey journey,
        ArrivalPrediction? prediction,
        double? distanceMiles,
        DateTimeOffset now)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("riderName", riderName ?? "Bus");
            writer.WriteString("stage", StageName(journey.Stage));
            writer.WriteString("basis", BasisName(prediction?.Basis));

            // Whole-second epoch: Swift's .iso8601 decoder rejects the
            // fractional seconds .NET writes by default.
            //
            // The prediction is only a FALLBACK for when no journey is running.
            // Once the rider is aboard it describes the next run, not this one,
            // and borrowing it puts the afternoon pickup under "riding to
            // school" on the home screen.
            bool aboard = journey.Stage is JourneyStage.ToSchool or JourneyStage.FromSchool;
            DateTimeOffset? target = journey.Target ?? (aboard ? null : prediction?.Arrival);
            if (target is not null)
            {
                writer.WriteNumber("target", target.Value.ToUnixTimeSeconds());
            }
            else
            {
                writer.WriteNull("target");
            }

            writer.WriteNumber("updatedAt", now.ToUnixTimeSeconds());

            // 0-100 through the current stage, so the widget can draw the route
            // track the card and the app both draw. A widget cannot work this
            // out for itself: it has no engine and no history.
            if (journey.Progress is int progress)
            {
                writer.WriteNumber("progress", progress);
            }
            else
            {
                writer.WriteNull("progress");
            }

            if (distanceMiles is double miles)
            {
                writer.WriteNumber("distanceMiles", Math.Round(miles, 2));
            }
            else
            {
                writer.WriteNull("distanceMiles");
            }

            if (busNumber is { Length: > 0 } bus)
            {
                writer.WriteString("busNumber", bus);
            }
            else
            {
                writer.WriteNull("busNumber");
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string StageName(JourneyStage stage) => stage switch
    {
        JourneyStage.ToStop => "to_stop",
        JourneyStage.AtStop => "at_stop",
        JourneyStage.ToSchool => "to_school",
        JourneyStage.AtSchool => "at_school",
        JourneyStage.FromSchool => "from_school",
        JourneyStage.Home => "home",
        _ => "idle",
    };

    private static string BasisName(PredictionBasis? basis) => basis switch
    {
        PredictionBasis.Route => "route",
        PredictionBasis.Approach => "approach",
        PredictionBasis.Historical => "historical",
        PredictionBasis.Scheduled => "scheduled",
        _ => "unknown",
    };
}
