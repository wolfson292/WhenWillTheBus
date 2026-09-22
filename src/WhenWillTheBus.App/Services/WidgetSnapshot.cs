// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Foundation;
using WhenWillTheBus.Core.Api;
using WhenWillTheBus.Core.Model;

namespace WhenWillTheBus.App.Services;

/// <summary>
/// Leaves the next arrival where the home-screen widget can find it.
/// </summary>
/// <remarks>
/// A widget cannot run the prediction: it wakes for a moment, draws, and goes
/// away. It has no network, no history and no engine. So the app writes a
/// snapshot into the shared App Group container after each poll, and the widget
/// reads it.
///
/// EVERY FIELD HERE MUST MATCH <c>BusSnapshot</c> in
/// ios/LiveActivity/BusSnapshot.swift. Two processes, two languages, and a
/// mismatch is a widget stuck on its placeholder with nothing explaining why.
/// </remarks>
public static partial class WidgetSnapshot
{
    /// <summary>Must match <c>BusSnapshot.appGroup</c> and both entitlements files.</summary>
    private const string AppGroup = "group.com.denlair.whenwillthebus";
    private const string Filename = "widget-snapshot.json";

    [LibraryImport("__Internal")]
    private static partial void wwtb_reload_widgets();

    /// <summary>Write the snapshot and ask WidgetKit to redraw.</summary>
    /// <remarks>
    /// Never throws. A widget that cannot be updated is a stale number on a home
    /// screen; an exception here would take down the poll that feeds everything
    /// else.
    /// </remarks>
    public static void Write(
        Student? rider,
        Journey journey,
        ArrivalPrediction? prediction,
        RiderInfo? info,
        DateTimeOffset now)
    {
        try
        {
            NSUrl? container = NSFileManager.DefaultManager.GetContainerUrl(AppGroup);
            if (container is null)
            {
                // The App Group is not configured on this build. The app works
                // fine; only the widget goes without.
                return;
            }

            string path = Path.Combine(container.Path!, Filename);
            File.WriteAllText(path, Serialise(rider, journey, prediction, info, now));

            wwtb_reload_widgets();
        }
        catch (Exception)
        {
            // Deliberately swallowed. See above.
        }
    }

    private static string Serialise(
        Student? rider,
        Journey journey,
        ArrivalPrediction? prediction,
        RiderInfo? info,
        DateTimeOffset now)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("riderName", rider?.Name ?? "Bus");
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

            if (info?.DistanceMiles is double miles)
            {
                writer.WriteNumber("distanceMiles", Math.Round(miles, 2));
            }
            else
            {
                writer.WriteNull("distanceMiles");
            }

            if (rider?.BusNumber is { Length: > 0 } bus)
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
