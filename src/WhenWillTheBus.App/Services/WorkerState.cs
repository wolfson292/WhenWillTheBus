// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.Json;
using WhenWillTheBus.Core.Api;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Prediction;

namespace WhenWillTheBus.App.Services;

/// <summary>One rider as the worker sees them.</summary>
public sealed record WorkerRider(
    Student Rider,
    RiderInfo? Reading,
    ArrivalPrediction? Prediction,
    Journey Journey,
    SchoolArrival? School);

/// <summary>
/// Reads the view the worker computed.
/// </summary>
/// <remarks>
/// A phone configured this way holds NO WheresTheBus password, which is the
/// point: the family's credentials live on one machine rather than on every
/// phone, and the third-party API sees one poller instead of four.
///
/// The shapes here mirror <c>RiderState.Serialise</c> on the worker. Both write
/// instants as whole-second epochs, for the same reason everything else here
/// does: Swift's decoder rejects the fractional seconds .NET writes, and using
/// one convention throughout means there is no second convention to get wrong.
/// </remarks>
public static class WorkerState
{
    public static IReadOnlyList<WorkerRider> Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        List<WorkerRider> riders = [];

        foreach (JsonElement element in document.RootElement.Array("riders"))
        {
            long? childId = element.Long("childId");
            if (childId is null)
            {
                continue;
            }

            riders.Add(new WorkerRider(
                ReadStudent(element, childId.Value),
                ReadReading(element),
                ReadPrediction(element),
                ReadJourney(element),
                ReadSchool(element)));
        }

        return riders;
    }

    private static Student ReadStudent(JsonElement element, long childId) => new()
    {
        ChildId = childId,
        Name = element.String("name") ?? $"Rider {childId}",
        BusNumber = element.String("busNumber"),
        SchoolName = element.String("schoolName"),
        StopAddress = element.String("stopAddress"),
        AmScheduled = ReadClock(element, "amScheduled"),
        PmScheduled = ReadClock(element, "pmScheduled"),
        Stop = ReadPoint(element.Property("reading") ?? default, "stop"),
        Scans = ReadScans(element),
    };

    private static IReadOnlyList<ScanEvent> ReadScans(JsonElement element)
    {
        List<ScanEvent> scans = [];

        foreach (JsonElement scan in element.Array("scans"))
        {
            long? at = scan.Long("at");
            if (at is null)
            {
                continue;
            }

            scans.Add(new ScanEvent(
                DateTimeOffset.FromUnixTimeSeconds(at.Value),
                scan.String("location"),
                string.Equals(scan.String("kind"), "dropoff", StringComparison.OrdinalIgnoreCase)
                    ? ScanKind.Dropoff
                    : ScanKind.Pickup,
                null));
        }

        return scans;
    }

    private static RiderInfo? ReadReading(JsonElement element)
    {
        if (element.Property("reading") is not { ValueKind: JsonValueKind.Object } reading)
        {
            return null;
        }

        return new RiderInfo
        {
            Bus = ReadPoint(reading, "bus"),
            Stop = ReadPoint(reading, "stop"),
            School = ReadPoint(reading, "school"),
            Home = ReadPoint(reading, "home"),
            DistanceMiles = reading.Double("distanceMiles"),
            Status = reading.String("status") switch
            {
                "current" => BusStatusKind.Current,
                "stale" => BusStatusKind.Stale,
                "inactive" => BusStatusKind.Inactive,
                _ => BusStatusKind.Unknown,
            },
            GpsAgeMinutes = (int?)reading.Long("gpsAgeMinutes"),
            RawStatus = reading.String("rawStatus"),
        };
    }

    private static ArrivalPrediction? ReadPrediction(JsonElement element)
    {
        if (element.Property("prediction") is not { ValueKind: JsonValueKind.Object } p)
        {
            return null;
        }

        DateTimeOffset? arrival = ReadInstant(p, "arrival");
        if (arrival is null)
        {
            return null;
        }

        return new ArrivalPrediction
        {
            Run = string.Equals(p.String("run"), "pm", StringComparison.OrdinalIgnoreCase) ? Run.Pm : Run.Am,
            Arrival = arrival.Value,
            Earliest = ReadInstant(p, "earliest"),
            Latest = ReadInstant(p, "latest"),
            Source = string.Equals(p.String("source"), "learned", StringComparison.OrdinalIgnoreCase)
                ? PredictionSource.Learned
                : PredictionSource.Scheduled,
            Basis = p.String("basis") switch
            {
                "route" => PredictionBasis.Route,
                "approach" => PredictionBasis.Approach,
                "historical" => PredictionBasis.Historical,
                _ => PredictionBasis.Scheduled,
            },
            Samples = (int)(p.Long("samples") ?? 0),
            Outliers = (int)(p.Long("outliers") ?? 0),
            SpreadMinutes = (int?)p.Long("spreadMinutes"),
            RouteSamples = (int?)p.Long("routeSamples"),
            AnchorSamples = (int?)p.Long("anchorSamples"),
            AnchoredAtMiles = p.Double("anchoredAtMiles"),
        };
    }

    private static Journey ReadJourney(JsonElement element)
    {
        if (element.Property("journey") is not { ValueKind: JsonValueKind.Object } j)
        {
            return Journey.Idle;
        }

        return new Journey
        {
            Stage = j.String("stage") switch
            {
                "to_stop" => JourneyStage.ToStop,
                "at_stop" => JourneyStage.AtStop,
                "to_school" => JourneyStage.ToSchool,
                "at_school" => JourneyStage.AtSchool,
                "from_school" => JourneyStage.FromSchool,
                "home" => JourneyStage.Home,
                _ => JourneyStage.Idle,
            },
            JourneyId = j.String("journeyId"),
            Target = ReadInstant(j, "target"),
            Boarded = ReadInstant(j, "boarded"),
            Progress = (int?)j.Long("progress"),
        };
    }

    private static SchoolArrival? ReadSchool(JsonElement element)
    {
        if (element.Property("school") is not { ValueKind: JsonValueKind.Object } s)
        {
            return null;
        }

        DateTimeOffset? arrival = ReadInstant(s, "arrival");
        return arrival is null
            ? null
            : new SchoolArrival(arrival.Value, (int)(s.Long("samples") ?? 0), (int?)s.Long("rideMinutes"));
    }

    private static GeoPoint? ReadPoint(JsonElement element, string name)
    {
        if (element.Property(name) is not { ValueKind: JsonValueKind.Array } pair
            || pair.GetArrayLength() < 2)
        {
            return null;
        }

        JsonElement[] parts = pair.EnumerateArray().ToArray();
        return new GeoPoint(parts[0].GetDouble(), parts[1].GetDouble());
    }

    private static DateTimeOffset? ReadInstant(JsonElement element, string name)
    {
        long? seconds = element.Long(name);
        return seconds is null ? null : DateTimeOffset.FromUnixTimeSeconds(seconds.Value);
    }

    private static TimeOnly? ReadClock(JsonElement element, string name) =>
        TimeOnly.TryParseExact(element.String(name), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly parsed)
            ? parsed
            : null;
}
