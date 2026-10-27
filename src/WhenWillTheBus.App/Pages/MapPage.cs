// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;
using WhenWillTheBus.App.Services;
using WhenWillTheBus.Core.Api;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Prediction;
using Map = Microsoft.Maui.Controls.Maps.Map;

namespace WhenWillTheBus.App.Pages;

/// <summary>
/// Where the bus is, against where it is going.
/// </summary>
/// <remarks>
/// A marker is the one thing a map does better than a sentence. Everything else
/// on this screen stays text, because distance is already the progress bar and
/// "4.5 miles from…" spent a whole line restating it.
///
/// THE BUS MARKER IS ONLY EVER AS GOOD AS ITS FIX. A stale reading is the last
/// known position repeated, not a new one, so the marker says how old it is
/// rather than implying the bus is there now.
/// </remarks>
public sealed class MapPage : ContentPage
{
    private readonly BusService _bus;
    private readonly Map _map;
    private readonly Label _caption = new() { FontSize = 13, Padding = new Thickness(16, 8) };

    private bool _centred;

    // What the route lines on the map were drawn from, so they are redrawn
    // only when that changes.
    private (Run Run, int Journeys, int Points)? _drawn;

    public MapPage(BusService bus)
    {
        _bus = bus;
        Title = "Map";

        _map = new Map
        {
            IsShowingUser = false,
            MapType = MapType.Street,
            IsZoomEnabled = true,
            IsScrollEnabled = true,
        };

        View legend = Legend();

        Content = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
            },
            Children = { _map, legend, _caption },
        };

        Grid.SetRow(_map, 0);
        Grid.SetRow(legend, 1);
        Grid.SetRow(_caption, 2);

        _bus.PropertyChanged += OnBusChanged;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Refresh();
    }

    private void OnBusChanged(object? sender, PropertyChangedEventArgs e) =>
        MainThread.BeginInvokeOnMainThread(Refresh);

    private void Refresh()
    {
        RiderInfo? info = _bus.Latest;
        _map.Pins.Clear();
        DrawRecordedRoutes();

        if (info is null)
        {
            _caption.Text = "Nothing to show yet.";
            return;
        }

        string rider = _bus.Rider?.Name?.Split(' ').FirstOrDefault() ?? "the rider";

        if (info.Stop is GeoPoint stop)
        {
            Add($"{rider}'s stop", _bus.Rider?.StopAddress ?? "Where the bus comes", stop, PinType.Place);
        }

        if (info.School is GeoPoint school)
        {
            Add(_bus.Rider?.SchoolName ?? "School", "School", school, PinType.Generic);
        }

        if (info.Bus is GeoPoint bus)
        {
            Add($"Bus {_bus.Rider?.BusNumber ?? "?"}", Freshness(info), bus, PinType.SavedPin);
        }

        _caption.Text = Caption(info);
        FrameBusAndStop(info);
    }

    /// <summary>
    /// Draw every recorded journey for this run, coloured by how often each
    /// stretch was driven.
    /// </summary>
    /// <remarks>
    /// Faint overlapping lines worked for a handful of journeys and stopped
    /// working at about ten: the usual road saturated, and a detour taken once
    /// was still a line of its own, so the two read alike. Counting the
    /// journeys over each stretch (<see cref="RouteHeat"/>) says which is which.
    ///
    /// Still each real journey, never an averaged line, which would draw a road
    /// the bus has never actually taken. And blue, not a yellow-to-red ramp:
    /// yellow is the bus and coral is a fault, so a heat scale in those would
    /// spend both on decoration. Deeper and thicker means more often.
    ///
    /// <para>
    /// DRAWN ONLY WHEN THE JOURNEYS CHANGE. Refresh runs on every property the
    /// service raises, several times a poll, and clearing and re-adding every
    /// overlay each time was affordable at one line per journey. At one per
    /// coloured stretch it blocked the main thread long enough on 26 Oct that
    /// iOS killed the app the moment the Map tab was tapped.
    /// </para>
    /// </remarks>
    private void DrawRecordedRoutes()
    {
        Run run = _bus.ShownRun;
        IReadOnlyList<IReadOnlyList<GeoPoint>> routes = _bus.RoutesFor(run);

        (Run, int, int) drawing = (run, routes.Count, routes.Sum(route => route.Count));
        if (_drawn == drawing)
        {
            return;
        }

        _drawn = drawing;
        _map.MapElements.Clear();

        if (routes.Count == 0)
        {
            return;
        }

        // Rarest first, so the usual road is drawn last and lies on top.
        foreach (RouteHeatSegment segment in RouteHeat.Build(routes))
        {
            (Color colour, float width) = Heat(segment.Level);
            Polyline line = new() { StrokeColor = colour, StrokeWidth = width };

            foreach (GeoPoint point in segment.Path)
            {
                line.Geopath.Add(new Location(point.Latitude, point.Longitude));
            }

            _map.MapElements.Add(line);
        }
    }

    /// <summary>
    /// One blue, getting LIGHTER, thicker and more solid as a road gets more
    /// usual, fading towards the map itself for the rare ones.
    /// </summary>
    /// <remarks>
    /// Lighter, not deeper: the map is dark, and on 26 Oct "most days" in a
    /// deep blue sank into it until every level looked the same.
    /// </remarks>
    private static (Color Colour, float Width) Heat(RouteHeatLevel level) => level switch
    {
        RouteHeatLevel.MostDays => (Color.FromArgb("#9CCBFF"), 7f),
        RouteHeatLevel.Often => (Color.FromRgba(0.31, 0.56, 0.90, 0.95), 5f),
        RouteHeatLevel.Sometimes => (Color.FromRgba(0.23, 0.37, 0.58, 0.85), 4f),
        _ => (Color.FromRgba(0.36, 0.39, 0.46, 0.75), 3f),
    };

    private static View Legend()
    {
        HorizontalStackLayout row = new() { Spacing = 12, Padding = new Thickness(16, 6, 16, 0) };

        foreach ((RouteHeatLevel level, string words) in (ReadOnlySpan<(RouteHeatLevel, string)>)
                 [(RouteHeatLevel.MostDays, "most days"), (RouteHeatLevel.Often, "often"),
                  (RouteHeatLevel.Sometimes, "sometimes"), (RouteHeatLevel.Rarely, "rarely")])
        {
            (Color colour, float width) = Heat(level);
            row.Children.Add(new HorizontalStackLayout
            {
                Spacing = 5,
                Children =
                {
                    new BoxView
                    {
                        Color = colour,
                        WidthRequest = 18,
                        HeightRequest = width,
                        CornerRadius = width / 2,
                        VerticalOptions = LayoutOptions.Center,
                    },
                    new Label { Text = words, FontSize = 12, VerticalOptions = LayoutOptions.Center },
                },
            });
        }

        return row;
    }

    private void Add(string label, string address, GeoPoint at, PinType type) =>
        _map.Pins.Add(new Pin
        {
            Label = label,
            Address = address,
            Type = type,
            Location = new Location(at.Latitude, at.Longitude),
        });

    /// <summary>
    /// Frame the bus and the stop together, once.
    /// </summary>
    /// <remarks>
    /// Only once: re-framing on every poll would yank the map out from under
    /// anyone who had panned or zoomed it, every thirty seconds.
    /// </remarks>
    private void FrameBusAndStop(RiderInfo info)
    {
        if (_centred)
        {
            return;
        }

        GeoPoint? anchor = info.Bus ?? info.Stop;
        if (anchor is not GeoPoint centre)
        {
            return;
        }

        double radius = 1.0;
        if (info.Bus is GeoPoint bus && info.Stop is GeoPoint stop)
        {
            centre = new GeoPoint((bus.Latitude + stop.Latitude) / 2, (bus.Longitude + stop.Longitude) / 2);

            // Half the separation is the radius that just contains both; the
            // margin keeps the pins off the edges.
            radius = Math.Max(Geo.DistanceMiles(bus, stop) * 0.75, 0.5);
        }

        _map.MoveToRegion(MapSpan.FromCenterAndRadius(
            new Location(centre.Latitude, centre.Longitude), Distance.FromMiles(radius)));

        _centred = true;
    }

    private string Caption(RiderInfo info)
    {
        List<string> parts = [];

        int routes = _bus.RoutesFor(_bus.ShownRun).Count;
        if (routes > 0)
        {
            parts.Add($"{routes} past {(routes == 1 ? "journey" : "journeys")} shown");
        }

        if (info.DistanceMiles is double miles)
        {
            parts.Add($"{miles:F1} mi from the stop");
        }

        parts.Add(info.Status switch
        {
            BusStatusKind.Current => "live",
            BusStatusKind.Stale => $"last seen {DateTime.Now.AddMinutes(-(info.GpsAgeMinutes ?? 0)):h:mm tt}",
            BusStatusKind.Inactive => "bus not reporting",
            _ => info.RawStatus ?? "unknown",
        });

        return string.Join("  ·  ", parts);
    }

    private static string Freshness(RiderInfo info) => info.Status switch
    {
        BusStatusKind.Current => "Live position",

        // Says its age rather than implying the bus is there now: a stale
        // reading is the last known position repeated.
        BusStatusKind.Stale => $"Where it was {info.GpsAgeMinutes} min ago",
        BusStatusKind.Inactive => "Not reporting — last known position",
        _ => "Position of unknown age",
    };
}
