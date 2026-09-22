// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using WhenWillTheBus.App.Services;
using WhenWillTheBus.Core.Api;
using WhenWillTheBus.Core.Model;

namespace WhenWillTheBus.App.Pages;

/// <summary>
/// The badge scans, newest first.
/// </summary>
/// <remarks>
/// The API returns bare "ID received" events with a location and a time; it does
/// NOT say whether the rider got on or off. The direction shown here is
/// inferred — see <see cref="ScanClassifier"/> — so it is labelled as on/off
/// rather than presented as something the school asserted.
///
/// The endpoint only ever returns the CURRENT DAY, so anything older than today
/// is here because this app kept it.
/// </remarks>
public sealed class ScansPage : ContentPage
{
    private readonly BusService _bus;
    private readonly CollectionView _list;
    private readonly Label _empty = new()
    {
        Text = "No scans yet.\n\nScans appear about five minutes after they happen, and the "
            + "school's own feed only ever returns today — everything older is kept by this app.",
        FontSize = 13,
        TextColor = Theme.TextDim,
        Padding = new Thickness(20),
        IsVisible = false,
    };

    public ScansPage(BusService bus)
    {
        _bus = bus;
        Title = "Scans";

        _list = new CollectionView
        {
            IsGrouped = true,
            SelectionMode = SelectionMode.None,
            ItemTemplate = new DataTemplate(Row),
            GroupHeaderTemplate = new DataTemplate(GroupHeader),
        };

        Content = new Grid { Children = { _list, _empty } };
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
        IReadOnlyList<ScanEvent> scans = _bus.Rider?.Scans ?? [];

        List<ScanDay> days = scans
            .OrderByDescending(scan => scan.Timestamp)
            .GroupBy(scan => scan.Timestamp.ToLocalTime().Date)
            .Select(group => new ScanDay(group.Key, group.ToList()))
            .ToList();

        _list.ItemsSource = days;
        _list.IsVisible = days.Count > 0;
        _empty.IsVisible = days.Count == 0;
    }

    private static View Row() =>
        new VerticalStackLayout
        {
            Padding = new Thickness(20, 10),
            Spacing = 2,
            Children =
            {
                Bound(new Label { FontSize = 16 }, Label.TextProperty, nameof(ScanEvent.Kind), Direction),
                Bound(new Label { FontSize = 13, TextColor = Theme.TextDim }, Label.TextProperty,
                    nameof(ScanEvent.Timestamp), When),
                Bound(new Label { FontSize = 12, TextColor = Theme.TextDim }, Label.TextProperty,
                    nameof(ScanEvent.Location), Place),
            },
        };

    private static View GroupHeader() =>
        Bound(
            new Label
            {
                FontSize = 13,
                FontAttributes = FontAttributes.Bold,
                Padding = new Thickness(20, 12, 20, 4),
                BackgroundColor = Theme.Ink800,
            },
            Label.TextProperty,
            nameof(ScanDay.Day),
            value => value is DateTime day ? Heading(day) : string.Empty);

    private static T Bound<T>(T view, BindableProperty property, string path, Func<object?, string> format)
        where T : BindableObject
    {
        view.SetBinding(property, new Binding(path, converter: new Formatter(format)));
        return view;
    }

    private static string Direction(object? value) =>
        value is ScanKind.Pickup ? "Got on the bus" : "Got off the bus";

    private static string When(object? value) =>
        value is DateTimeOffset at ? at.ToLocalTime().ToString("h:mm tt") : string.Empty;

    private static string Place(object? value) =>
        value as string is { Length: > 0 } place ? place : "Location not given";

    private static string Heading(DateTime day)
    {
        DateTime today = DateTime.Today;
        if (day == today)
        {
            return "Today";
        }

        return day == today.AddDays(-1) ? "Yesterday" : day.ToString("dddd d MMMM");
    }

    /// <summary>One day's scans. A List so CollectionView can group over it.</summary>
    private sealed class ScanDay(DateTime day, IEnumerable<ScanEvent> scans) : List<ScanEvent>(scans)
    {
        public DateTime Day { get; } = day;
    }

    private sealed class Formatter(Func<object?, string> format) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            format(value);

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
