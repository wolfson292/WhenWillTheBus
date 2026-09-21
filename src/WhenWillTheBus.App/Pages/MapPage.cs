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

        Content = new Grid
        {
            RowDefinitions = { new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto) },
            Children = { _map, _caption },
        };

        Grid.SetRow(_map, 0);
        Grid.SetRow(_caption, 1);

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
        Frame(info);
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
    private void Frame(RiderInfo info)
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
