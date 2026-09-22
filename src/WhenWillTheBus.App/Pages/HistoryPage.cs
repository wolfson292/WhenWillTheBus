// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using WhenWillTheBus.App.Services;
using WhenWillTheBus.Core.Model;

namespace WhenWillTheBus.App.Pages;

/// <summary>
/// What this app has actually watched the bus do.
/// </summary>
/// <remarks>
/// The published timetable can be badly wrong — the afternoon one here reads
/// nearly half an hour later than the bus really comes — and an estimate that
/// silently disagrees with the school's own number is hard to trust. So the
/// disagreement is shown, with what it rests on: how many arrivals, how much
/// they varied, and how many were set aside as bad days.
/// </remarks>
public sealed class HistoryPage : ContentPage
{
    private readonly BusService _bus;
    private readonly VerticalStackLayout _body = new() { Spacing = 4, Padding = new Thickness(20, 12) };

    public HistoryPage(BusService bus)
    {
        _bus = bus;
        Title = "History";
        Content = new ScrollView { Content = _body };
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
        _body.Children.Clear();

        IReadOnlyList<BusService.RunHistory> history = _bus.History();
        if (history.Count == 0 || history.All(run => run.Samples == 0))
        {
            _body.Children.Add(Note(
                "Nothing learned yet.\n\nPredictions work from two route samples and three arrivals, "
                + "so a fortnight of watching — or an import from the worker — is what turns the "
                + "timetable into a measurement."));
            return;
        }

        foreach (BusService.RunHistory run in history)
        {
            if (run.Samples == 0 && run.Arrivals.Count == 0)
            {
                continue;
            }

            _body.Children.Add(Heading(run.Run == Run.Am ? "Morning" : "Afternoon"));
            _body.Children.Add(Headline(run));

            if (run.SpreadMinutes is int spread)
            {
                string outliers = run.Outliers > 0
                    ? $", {run.Outliers} unusual day(s) set aside"
                    : string.Empty;
                _body.Children.Add(Note(
                    $"From {run.Samples} arrival(s), spread over {spread} minute(s){outliers}."));
            }

            View? accuracy = Accuracy(run.Arrivals);
            if (accuracy is not null)
            {
                _body.Children.Add(accuracy);
            }

            foreach (RunArrival arrival in run.Arrivals.Take(10))
            {
                _body.Children.Add(Row(arrival));
            }

            if (run.Arrivals.Count > 10)
            {
                _body.Children.Add(Note($"…and {run.Arrivals.Count - 10} older."));
            }
        }
    }

    /// <summary>
    /// How close the estimate has actually been, five minutes out.
    /// </summary>
    /// <remarks>
    /// Five minutes because that is when somebody decides to walk to the kerb.
    /// The LATE side is reported separately and deliberately: an estimate that
    /// runs early costs a few minutes waiting, and one that runs late means the
    /// bus has already gone.
    /// </remarks>
    private static View? Accuracy(IReadOnlyList<RunArrival> arrivals)
    {
        List<int> scored = arrivals
            .Where(arrival => arrival.ErrorAtFiveMinutes is not null)
            .Select(arrival => arrival.ErrorAtFiveMinutes!.Value)
            .ToList();

        if (scored.Count == 0)
        {
            return null;
        }

        double typical = scored.Select(Math.Abs).Order().ElementAt(scored.Count / 2) / 60.0;
        double worst = scored.Select(Math.Abs).Max() / 60.0;
        int late = scored.Count(error => error > 60);

        string text = $"Five minutes out, the estimate has been {typical:F1} min off typically, "
            + $"{worst:F1} min at worst, over {scored.Count} scored run(s).";

        if (late > 0)
        {
            text += $" {late} of those ran LATE by more than a minute.";
        }

        return new Label { Text = text, FontSize = 12, TextColor = Theme.TextDim, Margin = new Thickness(0, 2, 0, 6) };
    }

    /// <summary>The learned time against the published one — the point of the page.</summary>
    private static View Headline(BusService.RunHistory run)
    {
        if (run.Learned is not TimeOnly learned)
        {
            return Note(run.Scheduled is TimeOnly only
                ? $"Timetable says {only:h:mm tt}. Nothing observed yet."
                : "No timetable and nothing observed.");
        }

        string text = $"Usually arrives {learned:h:mm tt}";

        if (run.Scheduled is TimeOnly scheduled)
        {
            int drift = (int)(learned.ToTimeSpan() - scheduled.ToTimeSpan()).TotalMinutes;
            text += drift switch
            {
                0 => $", matching the {scheduled:h:mm tt} timetable.",
                < 0 => $" — {Math.Abs(drift)} min EARLIER than the {scheduled:h:mm tt} timetable.",
                _ => $" — {drift} min later than the {scheduled:h:mm tt} timetable.",
            };
        }

        return new Label { Text = text, FontSize = 15 };
    }

    private static View Row(RunArrival arrival)
    {
        DateTime local = arrival.Arrival.ToLocalTime().DateTime;

        List<string> notes = [];
        if (arrival.Substitute)
        {
            // Recorded so the day stays visible, but held out of what the
            // estimate learns from: a replacement vehicle keeps its own time.
            notes.Add("substitute, not learned from");
        }

        if (arrival.Track.Count > 0)
        {
            notes.Add($"{arrival.Track.Count} route points");
        }

        if (arrival.Recedes > 0)
        {
            notes.Add($"{arrival.Recedes} recede(s)");
        }

        if (arrival.ErrorAtFiveMinutes is int error)
        {
            double minutes = Math.Abs(error) / 60.0;
            string direction = error > 0 ? "late" : "early";
            notes.Add(Math.Abs(error) < 30 ? "estimate spot on" : $"estimate {minutes:F1} min {direction}");
        }

        string tail = notes.Count > 0 ? $"   ({string.Join(", ", notes)})" : string.Empty;

        return new Label
        {
            Text = $"{local:ddd d MMM}   {local:h:mm tt}{tail}",
            FontSize = 13,
            TextColor = arrival.Substitute ? Theme.TextDim : Theme.Text,
            Margin = new Thickness(0, 1),
        };
    }

    private static Label Heading(string text) =>
        new() { Text = text, FontSize = 17, FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 14, 0, 2) };

    private static Label Note(string text) =>
        new() { Text = text, FontSize = 12, TextColor = Theme.TextDim, Margin = new Thickness(0, 0, 0, 6) };
}
