// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.Json;
using WhenWillTheBus.Core;
using WhenWillTheBus.Core.Api;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Prediction;

namespace WhenWillTheBus.Server.Api;

/// <summary>
/// The app's Bus and Map tabs, as words and points for the /bus page.
/// </summary>
/// <remarks>
/// SAYS WHAT THE APP SAYS, WORD FOR WORD. Every sentence here is a copy of one
/// in BusPage or MapPage, and the rules for choosing between them are the same
/// rules: a laptop and a phone open side by side must not disagree about when
/// the bus is coming, or about how sure anybody is of it. Change one, change
/// both.
///
/// The page's script only lays this out and counts down; it decides nothing.
/// The decisions are C# here, against the same engine, so they are tested.
///
/// Colours travel as NAMES ("bus", "aboard", "far", "fault", "dim"), which the
/// page's stylesheet maps to the app's palette. The palette lives in one place
/// per platform, not in the data.
/// </remarks>
public static class BusView
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Inside this, an arrival is imminent enough to warm the interface (Theme.Soon).</summary>
    private static readonly TimeSpan Soon = TimeSpan.FromMinutes(10);

    /// <summary>The same ceiling as the Lock Screen card's BusPalette.countdownCeiling.</summary>
    private static readonly TimeSpan CountdownCeiling = TimeSpan.FromMinutes(20);

    /// <summary>Everything both tabs show except the recorded routes, which are heavy and change twice a day.</summary>
    public static string State(
        IReadOnlyDictionary<long, Student> students,
        PredictionEngine engine,
        LocalClock clock,
        DateTimeOffset now)
    {
        List<object> riders = [];

        foreach (Student student in students.Values)
        {
            ArrivalPrediction? prediction = engine.PredictNextArrival(student, now);
            SchoolArrival? school = SchoolArrivalPredictor.Predict(student, now, clock);
            Journey journey = engine.Stage(student, now, prediction, school?.Arrival);
            RiderInfo? info = engine.LatestFor(student.ChildId);
            Run shown = prediction?.Run ?? clock.RunOf(now);

            riders.Add(new
            {
                childId = student.ChildId,
                name = student.Name,
                bus = student.ShownBus,
                footer = Footer(student),
                card = Card(student, journey, prediction, info, school, clock, now),
                scans = Scans(student, clock, now),
                map = Map(student, info, Routes(engine, student, shown).Count, shown, clock, now),
            });
        }

        // The zone too, for the one time the page formats itself: a countdown more
        // than a day out reads as a weekday, and that is the household's weekday.
        return JsonSerializer.Serialize(new { now = now.ToUnixTimeSeconds(), zone = clock.Zone.Id, riders }, Json);
    }

    /// <summary>
    /// Every recorded journey for one run, coloured by how often each stretch
    /// was driven -- MapPage.DrawRecordedRoutes, as data.
    /// </summary>
    public static string RouteMap(Student student, PredictionEngine engine, Run run)
    {
        IReadOnlyList<IReadOnlyList<GeoPoint>> routes = Routes(engine, student, run);

        // Rarest first, so the usual road is drawn last and lies on top.
        var segments = RouteHeat.Build(routes).Select(segment => new
        {
            level = Level(segment.Level),
            path = segment.Path.Select(point => new[]
            {
                Math.Round(point.Latitude, Tuning.CoordinatePrecision),
                Math.Round(point.Longitude, Tuning.CoordinatePrecision),
            }),
        });

        return JsonSerializer.Serialize(new { run = run == Run.Am ? "am" : "pm", journeys = routes.Count, segments }, Json);
    }

    /// <summary>
    /// The journeys the map draws: BusService.RoutesFor.
    /// </summary>
    /// <remarks>
    /// The matched road where there is one, never a substitute's run, and not a
    /// run the review found to be GPS garbage -- one frozen morning would
    /// otherwise draw a road the bus never took, as boldly as any other.
    /// </remarks>
    public static IReadOnlyList<IReadOnlyList<GeoPoint>> Routes(PredictionEngine engine, Student student, Run run) =>
        engine.ArrivalsFor(student.ChildId)
            .Where(arrival => arrival.Run == run
                && !arrival.Substitute
                && arrival.Review?.Verdict is not (RunVerdict.BadGps or RunVerdict.Substitute)
                && (arrival.Road.Count > 1 || arrival.Track.Count > 1))
            .Select(arrival => arrival.Road.Count > 1
                ? arrival.Road
                : (IReadOnlyList<GeoPoint>)arrival.Track
                    .Select(point => new GeoPoint(point.Latitude, point.Longitude))
                    .ToList())
            .ToList();

    /// <summary>The Bus tab: BusPage.Refresh.</summary>
    private static object Card(
        Student student,
        Journey journey,
        ArrivalPrediction? prediction,
        RiderInfo? info,
        SchoolArrival? school,
        LocalClock clock,
        DateTimeOffset now)
    {
        if (prediction is null)
        {
            // Nothing to say. Say nothing, rather than an empty progress track
            // and a lone dash where a time should be.
            return new { estimate = false, stage = "No run scheduled." };
        }

        bool aboard = journey.Stage is JourneyStage.ToSchool or JourneyStage.FromSchool;
        Headline lead = Headline.For(journey, prediction.Arrival);
        bool reporting = info?.Status is BusStatusKind.Current;
        string accent = Accent(journey.Stage, lead.Moment, now);

        // The countdown is FORMATTED on the page, because it has to keep moving
        // between polls; what it counts to, and whether seconds are honest, is
        // decided here.
        object countdown = lead switch
        {
            { Kind: HeadlineKind.Now } => new { mode = "fixed", text = "at your stop", colour = accent },
            { Moment: DateTimeOffset moment } => new
            {
                mode = "to",
                at = moment.ToUnixTimeSeconds(),

                // Seconds only when something live is measuring them, and only
                // close in: a ticking clock off the timetable reads as a
                // measurement it is not.
                ticking = reporting
                    && prediction.Basis is PredictionBasis.Route or PredictionBasis.Approach
                    && moment - now > TimeSpan.Zero
                    && moment - now <= CountdownCeiling,
                colour = reporting ? accent : "dim",
            },
            _ when aboard && journey.Boarded is DateTimeOffset boarded =>
                new { mode = "fixed", text = $"aboard since {Clock(clock, boarded, "h:mm tt")}", colour = "aboard" },
            _ => new { mode = "none" },
        };

        // The observed range, not a statistical interval, and about the STOP --
        // so it says nothing about a ride in progress.
        string? band = lead.Kind is HeadlineKind.Time && !aboard
            && prediction.Earliest is DateTimeOffset early && prediction.Latest is DateTimeOffset late
            ? $"{Clock(clock, early, "h:mm")} – {Clock(clock, late, "h:mm")}"
            : null;

        (int filled, string colour, string label) = Confidence(prediction.Basis);

        return new
        {
            estimate = true,
            stage = StageTitle(journey.Stage, student.Name),
            headline = lead.Kind switch
            {
                HeadlineKind.Now => new { text = "now", meridiem = (string?)null },
                HeadlineKind.Time => new { text = Clock(clock, lead.Moment!.Value, "h:mm"), meridiem = (string?)Clock(clock, lead.Moment.Value, "tt") },
                _ => null,
            },
            countdown,
            band,

            // THE TRACK MEANS A DIFFERENT THING ONCE SHE IS ON THE BUS: it fills
            // across a journey rather than closing a distance.
            track = journey.Progress is int percent
                ? new
                {
                    percent = Math.Clamp(percent, 2, 98),
                    colour = reporting ? accent : "dim",
                    left = aboard
                        ? journey.Boarded is DateTimeOffset on ? $"on at {Clock(clock, on, "h:mm")}" : "aboard"
                        : info?.DistanceMiles is double miles ? $"{miles.ToString("F1", CultureInfo.InvariantCulture)} miles out" : string.Empty,
                    right = journey.Stage is JourneyStage.ToSchool ? "school" : "your stop",
                }
                : null,
            confidence = new { filled, colour, label, text = ConfidenceText(prediction) },
            facts = new
            {
                // The INSTANT, not an age: an age changes every minute a bus is running.
                gps = info is not null && info.Status != BusStatusKind.Unknown ? Freshness(info, clock, now) : "unknown",

                // Only a fault while a run is under way. Between runs there is no
                // bus to hear from, and a red lamp at lunchtime reads as broken.
                lamp = reporting ? "aboard" : journey.Active ? "fault" : "off",
                scans = ScanSummary(student, clock, now),
                school = school is null
                    ? null
                    : Clock(clock, school.Arrival, "h:mm tt") + (school.RideMinutes is int ride ? $" · {ride} min ride" : string.Empty),
            },
        };
    }

    /// <summary>The Map tab's pins and caption: MapPage.Refresh. The routes come separately.</summary>
    private static object? Map(Student student, RiderInfo? info, int journeys, Run run, LocalClock clock, DateTimeOffset now)
    {
        if (info is null)
        {
            return new { run = Name(run), caption = "Nothing to show yet." };
        }

        string first = student.Name?.Split(' ').FirstOrDefault() ?? "the rider";

        List<string> caption = [];
        if (journeys > 0)
        {
            caption.Add($"{journeys} past {(journeys == 1 ? "journey" : "journeys")} shown");
        }

        if (info.DistanceMiles is double miles)
        {
            caption.Add($"{miles.ToString("F1", CultureInfo.InvariantCulture)} mi from the stop");
        }

        caption.Add(info.Status switch
        {
            BusStatusKind.Current => "live",
            BusStatusKind.Stale => $"last seen {LastSeen(info, clock, now)}",
            BusStatusKind.Inactive => "bus not reporting",
            _ => info.RawStatus ?? "unknown",
        });

        return new
        {
            run = Name(run),
            journeys,
            caption = string.Join("  ·  ", caption),
            stop = Pin(info.Stop, $"{first}'s stop", student.StopAddress ?? "Where the bus comes"),
            school = Pin(info.School, student.SchoolName ?? "School", "School"),

            // A stale reading is the last known position repeated, so the pin
            // says how old it is rather than implying the bus is there now.
            bus = Pin(info.Bus, $"Bus {student.ShownBus ?? "?"}", info.Status switch
            {
                BusStatusKind.Current => "Live position",
                BusStatusKind.Stale => $"Where it was {info.GpsAgeMinutes} min ago",
                BusStatusKind.Inactive => "Not reporting — last known position",
                _ => "Position of unknown age",
            }),
            live = info.Status is BusStatusKind.Current,
        };
    }

    private static object? Pin(GeoPoint? at, string label, string detail) => at is GeoPoint point
        ? new
        {
            at = new[]
            {
                Math.Round(point.Latitude, Tuning.CoordinatePrecision),
                Math.Round(point.Longitude, Tuning.CoordinatePrecision),
            },
            label,
            detail,
        }
        : null;

    /// <summary>The badge scans, newest first, by day: ScansPage.</summary>
    private static object Scans(Student student, LocalClock clock, DateTimeOffset now)
    {
        DateOnly today = clock.DateOf(now);

        return student.Scans
            .OrderByDescending(scan => scan.Timestamp)
            .GroupBy(scan => clock.DateOf(scan.Timestamp))
            .Select(day => new
            {
                day = day.Key == today ? "Today"
                    : day.Key == today.AddDays(-1) ? "Yesterday"
                    : day.Key.ToString("dddd d MMMM", CultureInfo.InvariantCulture),
                scans = day.Select(scan => new
                {
                    // Inferred, not asserted: the feed says only "ID received".
                    what = scan.Kind is ScanKind.Pickup ? "Got on the bus" : "Got off the bus",
                    at = Clock(clock, scan.Timestamp, "h:mm tt"),
                    where = scan.Location is { Length: > 0 } place ? place : "Location not given",
                }),
            })
            .ToList();
    }

    public static string StageTitle(JourneyStage stage, string? name)
    {
        string rider = name ?? "Your rider";
        return stage switch
        {
            JourneyStage.ToStop => "Bus on the way to the stop",
            JourneyStage.AtStop => "The bus is at the stop",
            JourneyStage.ToSchool => $"{rider} is riding to school",
            JourneyStage.AtSchool => $"{rider} is at school",
            JourneyStage.FromSchool => $"{rider} is riding home",
            JourneyStage.Home => $"{rider} is home",
            _ => "Next bus",
        };
    }

    /// <summary>Theme.For: the colour this moment in the journey is entitled to.</summary>
    public static string Accent(JourneyStage stage, DateTimeOffset? target, DateTimeOffset now) => stage switch
    {
        JourneyStage.ToSchool or JourneyStage.AtSchool or JourneyStage.FromSchool or JourneyStage.Home => "aboard",
        JourneyStage.AtStop => "bus",
        JourneyStage.ToStop => target is { } due && due - now <= Soon ? "bus" : "far",
        _ => "far",
    };

    /// <summary>Theme.Confidence: how much the estimate is entitled to claim, out of four.</summary>
    public static (int Filled, string Colour, string Label) Confidence(PredictionBasis? basis) => basis switch
    {
        PredictionBasis.Route => (4, "bus", "on route"),
        PredictionBasis.Approach => (3, "bus", "estimated"),
        PredictionBasis.Historical => (2, "far", "usual time"),
        PredictionBasis.Scheduled => (1, "far", "timetable only"),
        _ => (0, "far", "nothing yet"),
    };

    private static string ConfidenceText(ArrivalPrediction prediction) => prediction.Basis switch
    {
        PredictionBasis.Route =>
            $"Matched against {prediction.RouteSamples} past journey(s) at this point on the route.",
        PredictionBasis.Approach =>
            $"Anchored {prediction.AnchoredAtMiles?.ToString("F1", CultureInfo.InvariantCulture)} miles out, from {prediction.AnchorSamples} past run(s).",
        PredictionBasis.Historical =>
            $"Usual time across {prediction.Samples} past arrival(s)"
            + (prediction.Outliers > 0 ? $", {prediction.Outliers} unusual day(s) set aside." : "."),
        _ => "Published timetable — nothing learned yet, and the timetable can be well out.",
    };

    private static string Freshness(RiderInfo info, LocalClock clock, DateTimeOffset now) => info.Status switch
    {
        BusStatusKind.Current => "live",
        BusStatusKind.Stale => $"last seen {LastSeen(info, clock, now)}",
        BusStatusKind.Inactive => "not reporting",
        _ => info.RawStatus ?? "unknown",
    };

    private static string LastSeen(RiderInfo info, LocalClock clock, DateTimeOffset now) =>
        Clock(clock, now.AddMinutes(-(info.GpsAgeMinutes ?? 0)), "h:mm tt");

    /// <summary>TODAY's last boarding and drop-off, because the line says today.</summary>
    private static string ScanSummary(Student student, LocalClock clock, DateTimeOffset now)
    {
        DateOnly today = clock.DateOf(now);
        ScanEvent? Last(ScanKind kind) => student.Scans.LastOrDefault(scan => scan.Kind == kind && clock.DateOf(scan.Timestamp) == today);

        List<string> parts = [];
        if (Last(ScanKind.Pickup) is { } pickup)
        {
            parts.Add($"on {Clock(clock, pickup.Timestamp, "h:mm tt")}");
        }

        if (Last(ScanKind.Dropoff) is { } dropoff)
        {
            parts.Add($"off {Clock(clock, dropoff.Timestamp, "h:mm tt")}");
        }

        return parts.Count == 0 ? "none today" : string.Join(", ", parts);
    }

    /// <summary>The school and the published timetable, small and last.</summary>
    private static string Footer(Student student)
    {
        List<string> parts = [];

        if (student.SchoolName is { Length: > 0 } school)
        {
            parts.Add(school);
        }

        List<string> timetable = [];
        if (student.AmScheduled is TimeOnly morning)
        {
            timetable.Add($"AM {morning.ToString("h:mm tt", CultureInfo.InvariantCulture)}");
        }

        if (student.PmScheduled is TimeOnly afternoon)
        {
            timetable.Add($"PM {afternoon.ToString("h:mm tt", CultureInfo.InvariantCulture)}");
        }

        if (timetable.Count > 0)
        {
            parts.Add($"timetable {string.Join(" / ", timetable)}");
        }

        return string.Join("  ·  ", parts);
    }

    /// <summary>In the household's time zone, not the server's or the laptop's.</summary>
    private static string Clock(LocalClock clock, DateTimeOffset instant, string format) =>
        clock.ToLocal(instant).ToString(format, CultureInfo.InvariantCulture);

    private static string Name(Run run) => run == Run.Am ? "am" : "pm";

    private static string Level(RouteHeatLevel level) => level switch
    {
        RouteHeatLevel.MostDays => "most",
        RouteHeatLevel.Often => "often",
        RouteHeatLevel.Sometimes => "sometimes",
        _ => "rarely",
    };
}
