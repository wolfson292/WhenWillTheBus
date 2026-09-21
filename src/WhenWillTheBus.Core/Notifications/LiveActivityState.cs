// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using WhenWillTheBus.Core.Model;

namespace WhenWillTheBus.Core.Notifications;

/// <summary>Something that can write itself as a Live Activity content state.</summary>
/// <remarks>
/// Written by hand rather than reflected over, so the payload's field names are
/// visibly the same ones the Swift <c>ContentState</c> decodes. A silent
/// mismatch here is a push that Apple accepts and the widget ignores, which is
/// among the least debuggable failures in the whole system.
/// </remarks>
public interface ILiveActivityState
{
    void Write(Utf8JsonWriter writer);
}

/// <summary>
/// What the Live Activity card shows.
/// </summary>
/// <remarks>
/// EVERY PROPERTY HERE MUST MATCH <c>BusActivityAttributes.ContentState</c> in
/// ios/LiveActivity/BusActivityAttributes.swift, name for name.
///
/// This lives in Core, and BOTH the phone and the worker serialise through it.
/// They each start and update the same card, so two implementations of this
/// shape would be two things to keep in step -- and a divergence presents as
/// a card that starts fine and then silently ignores every update, because
/// Apple accepts the push and the widget just cannot decode it.
/// </remarks>
public sealed record BusActivityState : ILiveActivityState
{
    public required JourneyStage Stage { get; init; }

    /// <summary>
    /// The instant being counted towards, ALREADY ROUNDED TO THE MINUTE.
    /// </summary>
    /// <remarks>
    /// Raw, it carries sub-second precision and moves on every poll, so anything
    /// watching it for a reason to re-push fires every thirty seconds to say the
    /// same thing. A card shows minutes.
    /// </remarks>
    public DateTimeOffset? Target { get; init; }

    public DateTimeOffset? Earliest { get; init; }

    public DateTimeOffset? Latest { get; init; }

    public int? Progress { get; init; }

    public double? DistanceMiles { get; init; }

    /// <summary>How the estimate was arrived at, so the card can be honest about it.</summary>
    public PredictionBasis? Basis { get; init; }

    /// <summary>
    /// The instant of the last GPS fix — NOT its age.
    /// </summary>
    /// <remarks>
    /// An age in minutes changes every single minute a bus is running; the OS
    /// renders "3 minutes ago" from an instant by itself, and only redraws when
    /// a new fix actually lands.
    /// </remarks>
    public DateTimeOffset? FixedAt { get; init; }

    /// <summary>The content state on its own, as the app hands it to ActivityKit.</summary>
    public string ToJson()
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            Write(writer);
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Build the state for a moment in a journey.</summary>
    public static BusActivityState For(
        Journey journey,
        ArrivalPrediction? prediction,
        double? distanceMiles,
        int? gpsAgeMinutes,
        DateTimeOffset now) => new()
        {
            Stage = journey.Stage,
            Target = journey.Target,
            Earliest = prediction?.Earliest,
            Latest = prediction?.Latest,
            Progress = journey.Progress,
            DistanceMiles = distanceMiles,
            Basis = prediction?.Basis,

            // The INSTANT of the fix, not its age: the OS renders "3 minutes ago"
            // from an instant by itself and redraws only when a new fix lands.
            FixedAt = gpsAgeMinutes is int age ? now.AddMinutes(-age) : now,
        };

    public void Write(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("stage", Name(Stage));
        WriteInstant(writer, "target", Target);
        WriteInstant(writer, "earliest", Earliest);
        WriteInstant(writer, "latest", Latest);
        WriteInstant(writer, "fixedAt", FixedAt);

        if (Progress is not null)
        {
            writer.WriteNumber("progress", Progress.Value);
        }
        else
        {
            writer.WriteNull("progress");
        }

        if (DistanceMiles is not null)
        {
            writer.WriteNumber("distanceMiles", Math.Round(DistanceMiles.Value, 2));
        }
        else
        {
            writer.WriteNull("distanceMiles");
        }

        writer.WriteString("basis", Basis is null ? "unknown" : Name(Basis.Value));
        writer.WriteEndObject();
    }

    /// <summary>
    /// Swift's <c>JSONDecoder</c> with <c>.iso8601</c> does not accept fractional
    /// seconds, so instants are written as whole-second epoch numbers and decoded
    /// as <c>TimeInterval</c>. Less pretty in a log, and it cannot be got wrong.
    /// </summary>
    private static void WriteInstant(Utf8JsonWriter writer, string name, DateTimeOffset? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteNumber(name, value.Value.ToUnixTimeSeconds());
        }
    }

    private static string Name(JourneyStage stage) => stage switch
    {
        JourneyStage.ToStop => "to_stop",
        JourneyStage.AtStop => "at_stop",
        JourneyStage.ToSchool => "to_school",
        JourneyStage.AtSchool => "at_school",
        JourneyStage.FromSchool => "from_school",
        JourneyStage.Home => "home",
        _ => "idle",
    };

    private static string Name(PredictionBasis basis) => basis switch
    {
        PredictionBasis.Route => "route",
        PredictionBasis.Approach => "approach",
        PredictionBasis.Historical => "historical",
        _ => "scheduled",
    };
}
