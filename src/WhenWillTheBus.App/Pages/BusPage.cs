// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using WhenWillTheBus.App.Services;
using WhenWillTheBus.Core.Api;
using WhenWillTheBus.Core.Model;

namespace WhenWillTheBus.App.Pages;

/// <summary>
/// What the app shows.
/// </summary>
/// <remarks>
/// LEAD WITH THE TIME, NOT THE DISTANCE. The distance is already drawn as the
/// progress bar, and "4.5 miles from…" spent the whole visible line restating
/// it — which matters most on the smallest screen, where it is also the only
/// line there is.
/// </remarks>
public sealed class BusPage : ContentPage
{
    private readonly BusService _bus;
    private readonly IServiceProvider _services;

    private readonly Label _stage = new() { FontSize = 15, TextColor = Colors.Gray };
    private readonly Label _arrival = new() { FontSize = 56, FontAttributes = FontAttributes.Bold };
    private readonly Label _countdown = new() { FontSize = 17 };
    private readonly Label _band = new() { FontSize = 13, TextColor = Colors.Gray };
    private readonly ProgressBar _progress = new() { HeightRequest = 6 };
    private readonly Label _distance = new() { FontSize = 14, TextColor = Colors.Gray };
    private readonly Label _confidence = new() { FontSize = 12, TextColor = Colors.Gray };
    private readonly Label _fix = new() { FontSize = 12, TextColor = Colors.Gray };
    private readonly Label _scans = new() { FontSize = 13, TextColor = Colors.Gray };
    private readonly Label _school = new() { FontSize = 13, TextColor = Colors.Gray };
    private readonly Label _rider = new() { FontSize = 12, TextColor = Colors.Gray };
    private readonly Label _problem = new() { FontSize = 13, TextColor = Colors.OrangeRed, IsVisible = false };
    private readonly Label _localOnly = new() { FontSize = 12, TextColor = Colors.DarkOrange, IsVisible = false };

    public BusPage(BusService bus, IServiceProvider services)
    {
        _bus = bus;
        _services = services;

        Title = "Bus";
        Padding = new Thickness(20, 16);

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Spacing = 10,
                Children =
                {
                    _problem,
                    _localOnly,
                    _stage,
                    _arrival,
                    _countdown,
                    _band,
                    new BoxView { HeightRequest = 6, Color = Colors.Transparent },
                    _progress,
                    _distance,
                    new BoxView { HeightRequest = 10, Color = Colors.Transparent },
                    _confidence,
                    _fix,
                    _scans,
                    _school,
                    new BoxView { HeightRequest = 6, Color = Colors.Transparent },
                    _rider,
                },
            },
        };

        _bus.PropertyChanged += OnBusChanged;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _bus.StartAsync();
        Refresh();
    }

    private void OnBusChanged(object? sender, PropertyChangedEventArgs e) =>
        MainThread.BeginInvokeOnMainThread(Refresh);

    private void Refresh()
    {
        _problem.Text = _bus.Problem;
        _problem.IsVisible = !string.IsNullOrEmpty(_bus.Problem);

        // A card nothing can update once the app is suspended will simply stop
        // advancing. Saying so beats leaving a parent to wonder why.
        _localOnly.IsVisible = _bus.LiveActivityIsLocalOnly;
        _localOnly.Text = _localOnly.IsVisible
            ? "The Lock Screen card only updates while this app is open."
            : string.Empty;

        Journey journey = _bus.Journey;
        ArrivalPrediction? prediction = _bus.Prediction;

        if (prediction is null)
        {
            // Nothing to say. Say nothing, rather than showing an empty
            // progress track and a lone dash where a time should be -- a
            // half-drawn card reads as a broken one.
            ShowEstimate(false);
            _stage.Text = string.IsNullOrEmpty(_bus.Problem) ? "No run scheduled." : string.Empty;
            _distance.Text = string.Empty;
            _fix.Text = string.Empty;
            _scans.Text = string.Empty;
            _school.Text = string.Empty;
            _rider.Text = RiderSummary(_bus.Rider);
            return;
        }

        ShowEstimate(true);
        _stage.Text = StageTitle(journey.Stage, _bus.Rider?.Name);

        DateTime local = prediction.Arrival.ToLocalTime().DateTime;
        _arrival.Text = local.ToString("h:mm");
        _countdown.Text = Countdown(prediction.Arrival);

        // The observed range, not a statistical interval. It closes towards
        // nothing as the bus nears the stop, because what is left to vary is the
        // part of the journey still to run.
        bool haveBand = prediction.Earliest is not null && prediction.Latest is not null;
        _band.Text = haveBand
            ? $"between {prediction.Earliest!.Value.ToLocalTime():h:mm} and {prediction.Latest!.Value.ToLocalTime():h:mm}"
            : string.Empty;
        _band.IsVisible = haveBand;

        _progress.Progress = (journey.Progress ?? 0) / 100.0;
        _progress.IsVisible = journey.Progress is not null;

        _distance.Text = _bus.Latest?.DistanceMiles is double miles
            ? $"{miles:F1} miles from the stop"
            : string.Empty;

        // Being honest about how the answer was reached. A window centred on a
        // timetable twenty minutes out is the failure that hides behind a
        // confident-looking number.
        _confidence.Text = Confidence(prediction);

        // The INSTANT, not an age: an age changes every minute a bus is running.
        _fix.Text = _bus.Latest is { } info && info.Status != BusStatusKind.Unknown
            ? $"Bus GPS: {Freshness(info)}"
            : string.Empty;

        _scans.Text = ScanSummary(_bus.Rider);
        _school.Text = SchoolSummary();
        _rider.Text = RiderSummary(_bus.Rider);
    }

    /// <summary>When the morning ride is expected to reach school.</summary>
    private string SchoolSummary()
    {
        if (_bus.School is not { } school)
        {
            return string.Empty;
        }

        string ride = school.RideMinutes is int minutes ? $", about a {minutes} min ride" : string.Empty;
        return $"At school around {school.Arrival.ToLocalTime():h:mm tt}{ride} "
            + $"(from {school.Samples} drop-off scan(s)).";
    }

    /// <summary>The unchanging facts, small and last: bus, school, timetable.</summary>
    private static string RiderSummary(Student? rider)
    {
        if (rider is null)
        {
            return string.Empty;
        }

        List<string> parts = [];

        if (rider.BusNumber is { Length: > 0 } bus)
        {
            parts.Add($"Bus {bus}");
        }

        if (rider.SchoolName is { Length: > 0 } school)
        {
            parts.Add(school);
        }

        // The published timetable, shown because it is what the estimate is
        // measured AGAINST -- the afternoon one here reads nearly half an hour
        // later than the bus actually comes.
        List<string> timetable = [];
        if (rider.AmScheduled is TimeOnly morning)
        {
            timetable.Add($"AM {morning:h:mm tt}");
        }

        if (rider.PmScheduled is TimeOnly afternoon)
        {
            timetable.Add($"PM {afternoon:h:mm tt}");
        }

        if (timetable.Count > 0)
        {
            parts.Add($"timetable {string.Join(" / ", timetable)}");
        }

        return string.Join("  ·  ", parts);
    }

    /// <summary>Show or hide everything that only means something with an estimate.</summary>
    private void ShowEstimate(bool visible)
    {
        _arrival.IsVisible = visible;
        _countdown.IsVisible = visible;
        _band.IsVisible = visible;
        _progress.IsVisible = visible;
        _confidence.IsVisible = visible;
        _school.IsVisible = visible;
    }

    private static string StageTitle(JourneyStage stage, string? name)
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

    private static string Countdown(DateTimeOffset arrival)
    {
        TimeSpan away = arrival - DateTimeOffset.Now;

        if (away <= TimeSpan.Zero)
        {
            return "due now";
        }

        if (away < TimeSpan.FromHours(1))
        {
            return $"in {(int)Math.Round(away.TotalMinutes)} min";
        }

        return away < TimeSpan.FromHours(20)
            ? $"in {away.Hours}h {away.Minutes}m"
            : arrival.ToLocalTime().ToString("dddd, h:mm tt");
    }

    private static string Confidence(ArrivalPrediction prediction) => prediction.Basis switch
    {
        PredictionBasis.Route =>
            $"Matched against {prediction.RouteSamples} past journey(s) at this point on the route.",
        PredictionBasis.Approach =>
            $"Anchored {prediction.AnchoredAtMiles:F1} miles out, from {prediction.AnchorSamples} past run(s).",
        PredictionBasis.Historical =>
            $"Usual time across {prediction.Samples} past arrival(s)"
            + (prediction.Outliers > 0 ? $", {prediction.Outliers} unusual day(s) set aside." : "."),
        _ => "Published timetable — nothing learned yet, and the timetable can be well out.",
    };

    private static string Freshness(RiderInfo info) => info.Status switch
    {
        BusStatusKind.Current => "live",
        BusStatusKind.Stale => $"last seen {DateTime.Now.AddMinutes(-(info.GpsAgeMinutes ?? 0)):h:mm tt}",
        BusStatusKind.Inactive => "not reporting",
        _ => info.RawStatus ?? "unknown",
    };

    private static string ScanSummary(Student? rider)
    {
        if (rider is null)
        {
            return string.Empty;
        }

        ScanEvent? pickup = rider.LastScanOf(ScanKind.Pickup);
        ScanEvent? dropoff = rider.LastScanOf(ScanKind.Dropoff);

        List<string> parts = [];
        if (pickup is not null)
        {
            parts.Add($"on at {pickup.Timestamp.ToLocalTime():h:mm tt}");
        }

        if (dropoff is not null)
        {
            parts.Add($"off at {dropoff.Timestamp.ToLocalTime():h:mm tt}");
        }

        return parts.Count == 0 ? "No badge scans today." : $"Scans: {string.Join(", ", parts)}.";
    }
}
