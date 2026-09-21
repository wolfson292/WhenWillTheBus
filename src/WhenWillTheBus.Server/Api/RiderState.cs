// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using System.Text.Json;
using WhenWillTheBus.Core;
using WhenWillTheBus.Core.Api;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Prediction;

namespace WhenWillTheBus.Server.Api;

/// <summary>
/// Everything a phone needs to draw its screens, computed here.
/// </summary>
/// <remarks>
/// This lets a phone run with NO WheresTheBus credentials at all — worth having
/// for three reasons. The family's password then lives in exactly one place
/// rather than on every phone. The third-party API sees one poller instead of
/// four, which matters when it is somebody else's service run for
/// schoolchildren. And a new phone is configured by pasting one worker address
/// instead of an account.
///
/// The phone still runs the same engine when it does have credentials; this is
/// an alternative source, not a replacement.
/// </remarks>
public static class RiderState
{
    public static string Serialise(
        IReadOnlyDictionary<long, Student> students,
        PredictionEngine engine,
        LocalClock clock,
        DateTimeOffset now)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("now", now.ToUnixTimeSeconds());
            writer.WriteStartArray("riders");

            foreach (Student student in students.Values)
            {
                ArrivalPrediction? prediction = engine.PredictNextArrival(student, now);
                SchoolArrival? school = SchoolArrivalPredictor.Predict(student, now, clock);
                Journey journey = engine.Stage(student, now, prediction, school?.Arrival);

                WriteRider(writer, student, engine.LatestFor(student.ChildId), prediction, journey, school, clock);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteRider(
        Utf8JsonWriter writer,
        Student student,
        RiderInfo? info,
        ArrivalPrediction? prediction,
        Journey journey,
        SchoolArrival? school,
        LocalClock clock)
    {
        writer.WriteStartObject();
        writer.WriteNumber("childId", student.ChildId);
        writer.WriteString("name", student.Name);
        WriteOptional(writer, "busNumber", student.BusNumber);
        WriteOptional(writer, "schoolName", student.SchoolName);
        WriteOptional(writer, "stopAddress", student.StopAddress);
        WriteClock(writer, "amScheduled", student.AmScheduled);
        WriteClock(writer, "pmScheduled", student.PmScheduled);

        writer.WriteStartObject("journey");
        writer.WriteString("stage", Name(journey.Stage));
        WriteOptional(writer, "journeyId", journey.JourneyId);
        WriteInstant(writer, "target", journey.Target);
        WriteInstant(writer, "boarded", journey.Boarded);

        if (journey.Progress is int progress)
        {
            writer.WriteNumber("progress", progress);
        }
        else
        {
            writer.WriteNull("progress");
        }

        writer.WriteEndObject();

        if (prediction is null)
        {
            writer.WriteNull("prediction");
        }
        else
        {
            writer.WriteStartObject("prediction");
            writer.WriteString("run", prediction.Run == Run.Am ? "am" : "pm");
            WriteInstant(writer, "arrival", prediction.Arrival);
            WriteInstant(writer, "earliest", prediction.Earliest);
            WriteInstant(writer, "latest", prediction.Latest);
            writer.WriteString("source", prediction.Source == PredictionSource.Learned ? "learned" : "scheduled");
            writer.WriteString("basis", Name(prediction.Basis));
            writer.WriteNumber("samples", prediction.Samples);
            writer.WriteNumber("outliers", prediction.Outliers);
            WriteNullableNumber(writer, "spreadMinutes", prediction.SpreadMinutes);
            WriteNullableNumber(writer, "routeSamples", prediction.RouteSamples);
            WriteNullableNumber(writer, "anchorSamples", prediction.AnchorSamples);

            if (prediction.AnchoredAtMiles is double rung)
            {
                writer.WriteNumber("anchoredAtMiles", rung);
            }
            else
            {
                writer.WriteNull("anchoredAtMiles");
            }

            writer.WriteEndObject();
        }

        if (school is null)
        {
            writer.WriteNull("school");
        }
        else
        {
            writer.WriteStartObject("school");
            WriteInstant(writer, "arrival", school.Arrival);
            writer.WriteNumber("samples", school.Samples);
            WriteNullableNumber(writer, "rideMinutes", school.RideMinutes);
            writer.WriteEndObject();
        }

        // The live reading, INCLUDING its freshness. A position without its age
        // is a claim about where the bus is now, and a stale one is not that.
        if (info is null)
        {
            writer.WriteNull("reading");
        }
        else
        {
            writer.WriteStartObject("reading");
            WritePoint(writer, "bus", info.Bus);
            WritePoint(writer, "stop", info.Stop);
            WritePoint(writer, "school", info.School);
            WritePoint(writer, "home", info.Home);

            if (info.DistanceMiles is double miles)
            {
                writer.WriteNumber("distanceMiles", Math.Round(miles, 3));
            }
            else
            {
                writer.WriteNull("distanceMiles");
            }

            writer.WriteString("status", info.Status.ToString().ToLowerInvariant());
            WriteNullableNumber(writer, "gpsAgeMinutes", info.GpsAgeMinutes);
            WriteOptional(writer, "rawStatus", info.RawStatus);
            writer.WriteEndObject();
        }

        writer.WriteStartArray("scans");
        foreach (ScanEvent scan in student.Scans.TakeLast(50))
        {
            writer.WriteStartObject();
            writer.WriteNumber("at", scan.Timestamp.ToUnixTimeSeconds());
            writer.WriteString("kind", scan.Kind == ScanKind.Pickup ? "pickup" : "dropoff");
            WriteOptional(writer, "location", scan.Location);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WritePoint(Utf8JsonWriter writer, string name, GeoPoint? point)
    {
        if (point is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WriteStartArray(name);
        writer.WriteNumberValue(Math.Round(point.Value.Latitude, Tuning.CoordinatePrecision));
        writer.WriteNumberValue(Math.Round(point.Value.Longitude, Tuning.CoordinatePrecision));
        writer.WriteEndArray();
    }

    /// <summary>
    /// Whole-second epoch, matching the Live Activity and the widget. Swift's
    /// .iso8601 decoder rejects the fractional seconds .NET writes by default.
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

    private static void WriteClock(Utf8JsonWriter writer, string name, TimeOnly? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value.Value.ToString("HH:mm", CultureInfo.InvariantCulture));
        }
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteNullableNumber(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteNumber(name, value.Value);
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
