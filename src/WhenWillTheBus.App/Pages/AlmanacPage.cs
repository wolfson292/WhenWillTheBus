// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.App.Services;
using WhenWillTheBus.Core.Api;
using WhenWillTheBus.Core.Model;

namespace WhenWillTheBus.App.Pages;

/// <summary>
/// The hidden one. Five taps on the rider's name.
/// </summary>
/// <remarks>
/// Every other screen answers "what is happening now". This one answers "what
/// has happened", which nothing else does and which turns out to be the more
/// affecting question: the app has been counting a child's school runs since
/// August, and the tally was sitting in the history file with nowhere to be
/// looked at.
///
/// Computed from what is already on the phone. It adds no polling, no storage
/// and no endpoint — which is why a bit of whimsy costs nothing here.
/// </remarks>
public sealed class AlmanacPage : ContentPage
{
    public AlmanacPage(BusService bus)
    {
        Title = "The bus, in numbers";
        BackgroundColor = Theme.Ink900;
        Padding = new Thickness(20, 16);

        string who = bus.Rider?.Name?.Split(' ').FirstOrDefault() ?? "Your rider";

        VerticalStackLayout body = new()
        {
            Spacing = 0,
            Children =
            {
                new Label
                {
                    Text = $"{who}'s bus",
                    FontSize = 26,
                    FontAttributes = FontAttributes.Bold,
                    TextColor = Theme.Text,
                },
                new Label
                {
                    Text = "Everything this app has watched happen.",
                    FontSize = 13,
                    TextColor = Theme.TextDim,
                    Margin = new Thickness(0, 2, 0, 18),
                },
            },
        };

        IReadOnlyList<BusService.RunHistory> history = bus.History();
        IReadOnlyList<RunArrival> arrivals = [.. history.SelectMany(run => run.Arrivals)];
        IReadOnlyList<ScanEvent> scans = bus.Rider?.Scans ?? [];

        if (arrivals.Count == 0 && scans.Count == 0)
        {
            body.Add(new Label
            {
                Text = "Nothing watched yet. Come back after a few school runs.",
                FontSize = 14,
                TextColor = Theme.TextMid,
            });

            Content = new ScrollView { Content = body };
            return;
        }

        body.Add(Stat("Journeys watched", arrivals.Count.ToString("N0")));

        HashSet<DateOnly> days = [.. arrivals.Select(a => DateOnly.FromDateTime(a.Arrival.ToLocalTime().DateTime))];
        if (days.Count > 0)
        {
            body.Add(Stat("School days seen", days.Count.ToString("N0")));

            DateOnly first = days.Min();
            body.Add(Stat("Watching since", first.ToString("d MMMM yyyy")));
        }

        // Pickup to drop-off on the same day, which is the only pairing that
        // means anything: a drop-off matched to the previous day's boarding
        // would report a twelve-hour ride.
        TimeSpan aboard = TimeSpan.Zero;
        int rides = 0;
        TimeSpan longest = TimeSpan.Zero;
        Dictionary<DateOnly, DateTimeOffset> boarded = [];
        foreach (ScanEvent scan in scans.OrderBy(scan => scan.Timestamp))
        {
            DateOnly day = DateOnly.FromDateTime(scan.Timestamp.ToLocalTime().DateTime);
            if (scan.Kind == ScanKind.Pickup)
            {
                boarded[day] = scan.Timestamp;
            }
            else if (boarded.TryGetValue(day, out DateTimeOffset on) && scan.Timestamp > on)
            {
                TimeSpan ride = scan.Timestamp - on;
                aboard += ride;
                rides++;
                longest = ride > longest ? ride : longest;
                boarded.Remove(day);
            }
        }

        if (rides > 0)
        {
            body.Add(Stat("Time on the bus", Spell(aboard), $"across {rides} ride(s) we have both scans for"));
            body.Add(Stat("Longest single ride", $"{(int)longest.TotalMinutes} minutes"));
        }

        foreach (BusService.RunHistory run in history.Where(run => run.Arrivals.Count > 0))
        {
            string name = run.Run == Run.Am ? "Morning" : "Afternoon";
            List<DateTimeOffset> times = [.. run.Arrivals.Select(a => a.Arrival)];

            TimeOnly earliest = times.Min(t => TimeOnly.FromDateTime(t.ToLocalTime().DateTime));
            TimeOnly latest = times.Max(t => TimeOnly.FromDateTime(t.ToLocalTime().DateTime));

            body.Add(Stat(
                $"{name} run",
                run.Learned is TimeOnly learned ? learned.ToString("h:mm tt") : "not learned yet",
                $"earliest {earliest:h:mm tt}, latest {latest:h:mm tt}"
                + (run.Scheduled is TimeOnly published ? $" · timetable says {published:h:mm tt}" : string.Empty)));
        }

        body.Add(new Label
        {
            Text = "Built by Dad, so nobody has to stand in the rain guessing.",
            FontSize = 12,
            TextColor = Theme.TextDim,
            HorizontalTextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 28, 0, 0),
        });

        Content = new ScrollView { Content = body };
    }

    private static View Stat(string name, string value, string? note = null)
    {
        VerticalStackLayout block = new()
        {
            Spacing = 1,
            Children =
            {
                new Label { Text = name, FontSize = 12, TextColor = Theme.TextDim },
                new Label
                {
                    Text = value,
                    FontSize = 22,
                    FontAttributes = FontAttributes.Bold,
                    TextColor = Theme.Bus,
                },
            },
        };

        if (note is not null)
        {
            block.Add(new Label { Text = note, FontSize = 11, TextColor = Theme.TextMid });
        }

        return new Border
        {
            Content = block,
            Padding = new Thickness(14, 12),
            Margin = new Thickness(0, 0, 0, 10),
            BackgroundColor = Theme.Ink800,
            Stroke = Theme.Ink700,
            StrokeThickness = 1,
        };
    }

    /// <summary>Hours and minutes, because "4,380 minutes" means nothing to anybody.</summary>
    private static string Spell(TimeSpan span)
    {
        int hours = (int)span.TotalHours;
        int minutes = span.Minutes;

        return hours switch
        {
            0 => $"{minutes} minutes",
            < 24 => $"{hours}h {minutes}m",
            _ => $"{hours / 24}d {hours % 24}h",
        };
    }
}
