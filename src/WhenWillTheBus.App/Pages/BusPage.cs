// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using Microsoft.Maui.Controls.Shapes;
using WhenWillTheBus.App.Services;
using WhenWillTheBus.Core.Api;
using WhenWillTheBus.Core.Model;

namespace WhenWillTheBus.App.Pages;

/// <summary>
/// What the app shows.
/// </summary>
/// <remarks>
/// LEAD WITH THE TIME, NOT THE DISTANCE. The distance is already drawn as the
/// route track, and "4.5 miles from…" spent the whole visible line restating
/// it — which matters most on the smallest screen, where it is also the only
/// line there is.
///
/// SAY HOW MUCH IS ACTUALLY KNOWN. The four-dot meter under the time is the
/// visible form of <see cref="PredictionBasis"/>. A timetable guess and a live
/// route match are the same shape of number and must never be the same shape of
/// thing on screen: the published afternoon time here reads nearly half an hour
/// later than the bus actually comes.
/// </remarks>
public sealed class BusPage : ContentPage
{
    private readonly BusService _bus;
    private readonly IServiceProvider _services;

    private readonly Label _rider = new()
    {
        FontSize = 17,
        FontAttributes = FontAttributes.Bold,
        TextColor = Theme.Text,
        VerticalOptions = LayoutOptions.Center,
    };

    private readonly Label _busNumber = new() { FontSize = 11, FontAttributes = FontAttributes.Bold, TextColor = Theme.TextMid };
    private readonly Border _busChip;

    private readonly Label _stage = new() { FontSize = 15, TextColor = Theme.TextDim };
    private readonly Label _arrival = new() { FontSize = 76, FontAttributes = FontAttributes.Bold, TextColor = Theme.Text };
    private readonly Label _meridiem = new() { FontSize = 24, FontAttributes = FontAttributes.Bold, TextColor = Theme.TextDim };
    private readonly Label _countdown = new() { FontSize = 22, FontAttributes = FontAttributes.Bold };
    private readonly Label _band = new() { FontSize = 14, TextColor = Theme.TextDim };

    private readonly Grid _track;
    private readonly ColumnDefinition _behind = new(new GridLength(1, GridUnitType.Star));
    private readonly ColumnDefinition _ahead = new(new GridLength(1, GridUnitType.Star));
    private readonly BoxView _trackFill = new() { HeightRequest = 6, CornerRadius = new CornerRadius(3), VerticalOptions = LayoutOptions.Center };
    private readonly Border _knob;
    private readonly BoxView _knobScreen = new() { HeightRequest = 5, WidthRequest = 14, CornerRadius = new CornerRadius(2.5), Color = Theme.Ink900 };
    private readonly Label _distance = new() { FontSize = 12, TextColor = Theme.TextDim };
    private readonly Label _stopCaption = new() { Text = "your stop", FontSize = 12, TextColor = Theme.TextDim, HorizontalOptions = LayoutOptions.End };
    private readonly VerticalStackLayout _trackBlock;

    private readonly BoxView[] _dots =
    [
        Dot(), Dot(), Dot(), Dot(),
    ];

    private readonly Label _basis = new() { FontSize = 13, FontAttributes = FontAttributes.Bold, VerticalOptions = LayoutOptions.Center };
    private readonly Label _confidence = new() { FontSize = 13, TextColor = Theme.TextMid };
    private readonly Border _confidenceCard;

    private readonly Label _fixValue = new() { FontSize = 13, TextColor = Theme.TextDim, HorizontalOptions = LayoutOptions.End };
    private readonly BoxView _fixLamp = new() { HeightRequest = 8, WidthRequest = 8, CornerRadius = new CornerRadius(4), VerticalOptions = LayoutOptions.Center };
    private readonly Label _scansValue = new() { FontSize = 13, TextColor = Theme.TextDim, HorizontalOptions = LayoutOptions.End };
    private readonly Label _schoolValue = new() { FontSize = 13, TextColor = Theme.TextDim, HorizontalOptions = LayoutOptions.End };
    private readonly Grid _schoolRow;
    private readonly BoxView _schoolRule;
    private readonly Border _factsCard;

    private readonly Label _footer = new() { FontSize = 12, TextColor = Theme.TextDim };
    private readonly Label _problem = new() { FontSize = 13, TextColor = Theme.Fault, IsVisible = false };
    private readonly Label _localOnly = new() { FontSize = 12, TextColor = Theme.Bus, IsVisible = false };

    public BusPage(BusService bus, IServiceProvider services)
    {
        _bus = bus;
        _services = services;

        Title = "Bus";
        BackgroundColor = Theme.Ink900;
        Padding = 0;

        // This screen draws its own header — the rider's name and their bus —
        // so the navigation bar above it would be a second, emptier copy of the
        // same thing. Title is still set, because the TAB reads it.
        NavigationPage.SetHasNavigationBar(this, false);

        _busChip = new Border
        {
            BackgroundColor = Theme.Ink700,
            Stroke = Colors.Transparent,
            StrokeShape = new RoundRectangle { CornerRadius = 6 },
            Padding = new Thickness(8, 3),
            VerticalOptions = LayoutOptions.Center,
            Content = _busNumber,
        };

        _knob = new Border
        {
            HeightRequest = 30,
            WidthRequest = 30,
            Stroke = Theme.Ink900,
            StrokeThickness = 4,
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            Padding = 0,
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Center,

            // Half the knob's width, so it straddles the head of the fill
            // rather than stopping short of it.
            Margin = new Thickness(0, 0, -15, 0),
            Content = new Grid { Children = { _knobScreen } },
        };

        _track = BuildTrack();

        _trackBlock = new VerticalStackLayout
        {
            Spacing = 4,
            Margin = new Thickness(0, 28, 0, 0),
            Children =
            {
                _track,
                new Grid { Children = { _distance, _stopCaption } },
            },
        };

        _confidenceCard = Card(new VerticalStackLayout
        {
            Spacing = 9,
            Children =
            {
                new HorizontalStackLayout
                {
                    Spacing = 5,
                    Children = { _dots[0], _dots[1], _dots[2], _dots[3], Spacer(4), _basis },
                },
                _confidence,
            },
        });

        _schoolRule = Rule();
        _schoolRow = Row("Reached school", _schoolValue);

        _factsCard = Card(new VerticalStackLayout
        {
            Spacing = 0,
            Children =
            {
                Row("Bus GPS", _fixValue, _fixLamp),
                Rule(),
                Row("Scans today", _scansValue),
                _schoolRule,
                _schoolRow,
            },
        });

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(20, 8, 20, 28),
                Spacing = 0,
                Children =
                {
                    Header(),
                    _problem,
                    _localOnly,
                    new VerticalStackLayout
                    {
                        Spacing = 0,
                        Margin = new Thickness(0, 26, 0, 0),
                        Children =
                        {
                            _stage,
                            new HorizontalStackLayout
                            {
                                Margin = new Thickness(0, 6, 0, 0),
                                Children = { _arrival, Meridiem() },
                            },
                            new HorizontalStackLayout
                            {
                                Spacing = 10,
                                Margin = new Thickness(0, 8, 0, 0),
                                Children = { _countdown, BandHolder() },
                            },
                        },
                    },
                    _trackBlock,
                    Spacer(22),
                    _confidenceCard,
                    Spacer(12),
                    _factsCard,
                    Spacer(14),
                    _footer,
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
        _problem.Margin = new Thickness(0, _problem.IsVisible ? 14 : 0, 0, 0);

        // A card nothing can update once the app is suspended will simply stop
        // advancing. Saying so beats leaving a parent to wonder why.
        _localOnly.IsVisible = _bus.LiveActivityIsLocalOnly;
        _localOnly.Text = _localOnly.IsVisible
            ? "The Lock Screen card only updates while this app is open."
            : string.Empty;
        _localOnly.Margin = new Thickness(0, _localOnly.IsVisible ? 10 : 0, 0, 0);

        Student? rider = _bus.Rider;
        _rider.Text = rider?.Name ?? "Bus";
        _busChip.IsVisible = rider?.BusNumber is { Length: > 0 };
        _busNumber.Text = rider?.BusNumber is { Length: > 0 } number ? $"Bus {number}" : string.Empty;
        _footer.Text = FooterSummary(rider);

        Journey journey = _bus.Journey;
        ArrivalPrediction? prediction = _bus.Prediction;

        if (prediction is null)
        {
            // Nothing to say. Say nothing, rather than showing an empty progress
            // track and a lone dash where a time should be — a half-drawn card
            // reads as a broken one.
            ShowEstimate(false);
            _stage.Text = string.IsNullOrEmpty(_bus.Problem) ? "No run scheduled." : string.Empty;
            _factsCard.IsVisible = false;
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        ShowEstimate(true);
        _factsCard.IsVisible = true;
        _stage.Text = StageTitle(journey.Stage, rider?.Name);

        // WHICH TIME TO LEAD WITH IS A RULE, NOT A CONDITION HERE -- see
        // Headline.For, which is pure and tested because this went wrong twice.
        // The prediction answers "when does the bus next reach the home stop"
        // and rolls to the following run the moment this one is under way, so
        // a live journey that borrows it is always wrong by most of a day.
        bool aboard = journey.Stage is JourneyStage.ToSchool or JourneyStage.FromSchool;
        Headline lead = Headline.For(journey, prediction.Arrival);
        DateTimeOffset? headline = lead.Moment;

        // The bus going quiet is a state, not a failure to draw. The last
        // estimate stays up but loses its colour, so the screen says "this is
        // what I knew" instead of counting confidently down to a time nothing is
        // backing any more.
        bool reporting = _bus.Latest?.Status is BusStatusKind.Current;
        Color accent = Theme.For(journey.Stage, headline, now);
        Color trackColour = reporting ? accent : Theme.Ink600;

        _arrival.IsVisible = lead.Kind is not HeadlineKind.None;
        _meridiem.IsVisible = lead.Kind is HeadlineKind.Time;

        if (lead.Kind is HeadlineKind.Now)
        {
            // The bus is HERE, and there is nothing to count down to. The Lock
            // Screen card has always said "now" at this moment; the app said
            // "5:21 PM, in 9h 13m".
            _arrival.Text = "now";
            _countdown.Text = "at your stop";
            _countdown.TextColor = accent;
            _countdown.IsVisible = true;
        }
        else if (headline is { } moment)
        {
            DateTimeOffset local = moment.ToLocalTime();
            _arrival.Text = local.ToString("h:mm");
            _meridiem.Text = local.ToString("tt");
            _countdown.Text = Countdown(moment);
            _countdown.TextColor = reporting ? accent : Theme.TextDim;
            _countdown.IsVisible = true;
        }
        else if (aboard && journey.Boarded is { } boarded)
        {
            // Nothing has been learned about where this ride ends yet, so say
            // the one thing that IS known rather than borrowing a number from a
            // different journey.
            _countdown.Text = $"aboard since {boarded.ToLocalTime():h:mm tt}";
            _countdown.TextColor = Theme.Aboard;
            _countdown.IsVisible = true;
        }
        else
        {
            _countdown.IsVisible = false;
        }

        // The observed range, not a statistical interval. It closes towards
        // nothing as the bus nears the stop, because what is left to vary is the
        // part of the journey still to run. It describes the arrival at the
        // STOP, so it says nothing about a ride in progress.
        bool haveBand = lead.Kind is HeadlineKind.Time
            && !aboard && prediction.Earliest is not null && prediction.Latest is not null;
        _band.Text = haveBand
            ? $"{prediction.Earliest!.Value.ToLocalTime():h:mm} – {prediction.Latest!.Value.ToLocalTime():h:mm}"
            : string.Empty;
        _band.IsVisible = haveBand;

        DrawTrack(journey.Progress, trackColour);

        _distance.Text = _bus.Latest?.DistanceMiles is double miles
            ? $"{miles:F1} miles out"
            : string.Empty;

        // Being honest about how the answer was reached. A window centred on a
        // timetable twenty minutes out is the failure that hides behind a
        // confident-looking number.
        (int filled, Color colour, string label) = Theme.Confidence(prediction.Basis);
        for (int i = 0; i < _dots.Length; i++)
        {
            _dots[i].Color = i < filled ? colour : Theme.Ink600;
        }

        _basis.Text = label;
        _basis.TextColor = colour == Theme.Bus ? Theme.Bus : Theme.Text;
        _confidence.Text = Confidence(prediction);

        // The INSTANT, not an age: an age changes every minute a bus is running.
        _fixValue.Text = _bus.Latest is { } info && info.Status != BusStatusKind.Unknown
            ? Freshness(info)
            : "unknown";

        // A BUS THAT IS NOT REPORTING IS ONLY A FAULT WHILE A RUN IS UNDER WAY.
        // Between the morning and the afternoon there is no bus to hear from,
        // and a red lamp against "unknown" at lunchtime reads as something
        // broken rather than as a fleet that is parked.
        _fixLamp.Color = reporting
            ? Theme.Aboard
            : journey.Active ? Theme.Fault : Theme.Ink600;

        _scansValue.Text = ScanSummary(rider);

        string school = SchoolSummary();
        _schoolValue.Text = school;
        _schoolRow.IsVisible = school.Length > 0;
        _schoolRule.IsVisible = school.Length > 0;
    }

    /// <summary>Lay the fill, the knob and the stop marker out along the route.</summary>
    /// <remarks>
    /// Two star columns split at the progress fraction, so the head of the fill
    /// is a layout boundary rather than a measured pixel offset — which means it
    /// lands correctly on every screen width without anyone computing one.
    ///
    /// The fraction is clamped off both ends. A zero-star column collapses to
    /// nothing and takes the knob's alignment with it, which puts a bus marker
    /// half off the left edge of the phone.
    /// </remarks>
    private void DrawTrack(int? progress, Color colour)
    {
        _trackBlock.IsVisible = progress is not null;
        if (progress is not int percent)
        {
            return;
        }

        double done = Math.Clamp(percent / 100.0, 0.02, 0.98);
        _behind.Width = new GridLength(done, GridUnitType.Star);
        _ahead.Width = new GridLength(1 - done, GridUnitType.Star);

        _trackFill.Color = colour;
        _knob.BackgroundColor = colour;
    }

    private Grid BuildTrack()
    {
        BoxView back = new()
        {
            HeightRequest = 6,
            CornerRadius = new CornerRadius(3),
            Color = Theme.Ink700,
            VerticalOptions = LayoutOptions.Center,
        };

        Border stop = new()
        {
            HeightRequest = 20,
            WidthRequest = 20,
            Stroke = Theme.Ink600,
            StrokeThickness = 4,
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            BackgroundColor = Theme.Ink900,
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Center,
        };

        Grid track = new()
        {
            HeightRequest = 30,
            ColumnDefinitions = { _behind, _ahead },
        };

        track.Add(back);
        Grid.SetColumnSpan(back, 2);
        track.Add(_trackFill);
        track.Add(_knob);
        track.Add(stop, 1);

        return track;
    }

    private View Header()
    {
        Grid header = new()
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
            },
            ColumnSpacing = 10,
            HeightRequest = 34,
        };

        header.Add(_rider);
        header.Add(_busChip, 1);
        return header;
    }

    private View Meridiem() => new VerticalStackLayout
    {
        VerticalOptions = LayoutOptions.End,
        Margin = new Thickness(6, 0, 0, 14),
        Children = { _meridiem },
    };

    private View BandHolder() => new VerticalStackLayout
    {
        VerticalOptions = LayoutOptions.End,
        Margin = new Thickness(0, 0, 0, 3),
        Children = { _band },
    };

    private static Border Card(View content) => new()
    {
        BackgroundColor = Theme.Ink800,
        Stroke = Colors.Transparent,
        StrokeShape = new RoundRectangle { CornerRadius = 16 },
        Padding = new Thickness(16, 15),
        Content = content,
    };

    /// <summary>One fact: a name on the left, the value on the right.</summary>
    private static Grid Row(string name, Label value, View? lamp = null)
    {
        Grid row = new()
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
            },
            ColumnSpacing = 9,
            Padding = new Thickness(0, 11),
        };

        if (lamp is not null)
        {
            row.Add(lamp);
        }

        row.Add(new Label { Text = name, FontSize = 14, TextColor = Theme.Text, VerticalOptions = LayoutOptions.Center }, 1);
        row.Add(value, 2);
        return row;
    }

    private static BoxView Rule() => new()
    {
        HeightRequest = 1,
        Color = Theme.Ink700,
    };

    private static BoxView Dot() => new()
    {
        HeightRequest = 7,
        WidthRequest = 7,
        CornerRadius = new CornerRadius(4),
        Color = Theme.Ink600,
        VerticalOptions = LayoutOptions.Center,
    };

    private static BoxView Spacer(double height) => new()
    {
        HeightRequest = height,
        Color = Colors.Transparent,
    };

    /// <summary>When the morning ride is expected to reach school.</summary>
    private string SchoolSummary()
    {
        if (_bus.School is not { } school)
        {
            return string.Empty;
        }

        string ride = school.RideMinutes is int minutes ? $" · {minutes} min ride" : string.Empty;
        return $"{school.Arrival.ToLocalTime():h:mm tt}{ride}";
    }

    /// <summary>The unchanging facts, small and last: school and timetable.</summary>
    private static string FooterSummary(Student? rider)
    {
        if (rider is null)
        {
            return string.Empty;
        }

        List<string> parts = [];

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
        _meridiem.IsVisible = visible;
        _countdown.IsVisible = visible;
        _band.IsVisible = visible;
        _trackBlock.IsVisible = visible;
        _confidenceCard.IsVisible = visible;
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

        // TODAY, because the line says today. Reading the last scan of each
        // kind whenever it happened put YESTERDAY afternoon's boarding and
        // yesterday morning's drop-off under "Scans today" at 08:07 -- which
        // reads as a journey that has already finished, on a morning where the
        // rider had not yet got on the bus.
        DateTime today = DateTime.Now.Date;
        ScanEvent? pickup = LastToday(rider, ScanKind.Pickup, today);
        ScanEvent? dropoff = LastToday(rider, ScanKind.Dropoff, today);

        List<string> parts = [];
        if (pickup is not null)
        {
            parts.Add($"on {pickup.Timestamp.ToLocalTime():h:mm tt}");
        }

        if (dropoff is not null)
        {
            parts.Add($"off {dropoff.Timestamp.ToLocalTime():h:mm tt}");
        }

        return parts.Count == 0 ? "none today" : string.Join(", ", parts);
    }

    /// <summary>The most recent scan of a kind that happened on a given local day.</summary>
    private static ScanEvent? LastToday(Student rider, ScanKind kind, DateTime day)
    {
        for (int index = rider.Scans.Count - 1; index >= 0; index--)
        {
            ScanEvent scan = rider.Scans[index];
            if (scan.Kind == kind && scan.Timestamp.ToLocalTime().Date == day)
            {
                return scan;
            }
        }

        return null;
    }
}
