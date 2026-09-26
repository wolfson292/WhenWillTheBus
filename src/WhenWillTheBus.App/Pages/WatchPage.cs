// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.App.Services;

namespace WhenWillTheBus.App.Pages;

/// <summary>
/// Ask for a film or a series.
/// </summary>
/// <remarks>
/// The point of this screen is that nobody has to learn anything. Type a name,
/// tap the poster, done — no quality profile, no root folder, no account on a
/// second service with a second password. Everything that would otherwise be a
/// question is decided on the worker.
///
/// The phone holds NO media-server credentials and never reaches Radarr or
/// Sonarr. It asks the worker, which is already the one thing in this system
/// trusted with keys.
/// </remarks>
public sealed class WatchPage : ContentPage
{
    private readonly MediaClient _media;

    private readonly SearchBar _search = new()
    {
        Placeholder = "A film or a series…",
        TextColor = Theme.Text,
        PlaceholderColor = Theme.TextDim,
        CancelButtonColor = Theme.Bus,
    };

    private readonly ActivityIndicator _busy = new() { Color = Theme.Bus, IsVisible = false };
    private readonly VerticalStackLayout _results = new() { Spacing = 8 };
    private readonly Label _message = new()
    {
        FontSize = 14,
        TextColor = Theme.TextDim,
        Padding = new Thickness(4, 12),
    };

    private CancellationTokenSource? _inFlight;

    public WatchPage(MediaClient media)
    {
        _media = media;
        Title = "Watch";
        Padding = new Thickness(16, 8);

        _search.SearchButtonPressed += async (_, _) => await SearchAsync();
        _search.TextChanged += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.NewTextValue))
            {
                ShowRecentAsync().FireAndForget();
            }
        };

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Spacing = 4,
                Children = { _search, _busy, _message, _results },
            },
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        ShowRecentAsync().FireAndForget();
    }

    private async Task SearchAsync()
    {
        string term = _search.Text?.Trim() ?? string.Empty;
        if (term.Length < 2)
        {
            return;
        }

        // One search at a time. Typing quickly otherwise leaves two answers
        // racing, and the slower one wins because it lands last.
        _inFlight?.Cancel();
        CancellationTokenSource mine = new();
        _inFlight = mine;

        _results.Clear();
        _message.Text = string.Empty;
        _busy.IsVisible = _busy.IsRunning = true;

        try
        {
            IReadOnlyList<WatchResult> found = await _media.SearchAsync(term, cancellationToken: mine.Token);
            if (mine.IsCancellationRequested)
            {
                return;
            }

            _busy.IsVisible = _busy.IsRunning = false;

            if (found.Count == 0)
            {
                _message.Text = $"Nothing found for \"{term}\".";
                return;
            }

            // Things already owned sink to the bottom: they are worth showing —
            // "we have this" answers the question — but they are not what
            // somebody searching is trying to do.
            foreach (WatchResult result in found.OrderBy(result => result.AlreadyHave))
            {
                _results.Add(Card(result));
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer search.
        }
        finally
        {
            if (ReferenceEquals(_inFlight, mine))
            {
                _busy.IsVisible = _busy.IsRunning = false;
            }
        }
    }

    private async Task ShowRecentAsync()
    {
        _results.Clear();
        IReadOnlyList<WatchRequest> recent = await _media.RecentAsync();

        if (recent.Count == 0)
        {
            _message.Text = "Search for something and tap it. It starts downloading straight away.";
            return;
        }

        _message.Text = "Recently asked for";
        foreach (WatchRequest request in recent.Take(20))
        {
            _results.Add(HistoryRow(request));
        }
    }

    /// <summary>One searchable result, as a tappable card.</summary>
    private View Card(WatchResult result)
    {
        Label outcome = new() { FontSize = 12, TextColor = Theme.TextDim, IsVisible = false };

        Grid card = new()
        {
            ColumnDefinitions = [new ColumnDefinition(64), new ColumnDefinition(GridLength.Star)],
            ColumnSpacing = 12,
            Padding = new Thickness(10),
            BackgroundColor = Theme.Ink800,
        };

        card.Add(Poster(result.PosterUrl), 0);
        card.Add(new VerticalStackLayout
        {
            Spacing = 2,
            Children =
            {
                new Label
                {
                    Text = result.Label,
                    FontSize = 15,
                    FontAttributes = FontAttributes.Bold,
                    TextColor = Theme.Text,
                },
                new Label
                {
                    Text = result.AlreadyHave
                        ? (result.IsSeries ? "Series · already in the library" : "Film · already in the library")
                        : (result.IsSeries ? "Series" : "Film"),
                    FontSize = 12,
                    TextColor = result.AlreadyHave ? Theme.Aboard : Theme.TextDim,
                },
                new Label
                {
                    Text = result.Overview ?? string.Empty,
                    FontSize = 12,
                    TextColor = Theme.TextMid,
                    MaxLines = 3,
                    LineBreakMode = LineBreakMode.TailTruncation,
                    IsVisible = !string.IsNullOrWhiteSpace(result.Overview),
                },
                outcome,
            },
        }, 1);

        if (!result.AlreadyHave)
        {
            TapGestureRecognizer tap = new();
            tap.Tapped += async (_, _) =>
            {
                card.Opacity = 0.5;
                outcome.IsVisible = true;
                outcome.Text = "Asking…";
                outcome.TextColor = Theme.TextDim;

                string said = await _media.RequestAsync(result);

                card.Opacity = 1;
                outcome.Text = said;
                outcome.TextColor = said.StartsWith("Added", StringComparison.Ordinal)
                    ? Theme.Aboard
                    : Theme.Fault;
            };

            card.GestureRecognizers.Add(tap);
        }

        return card;
    }

    private static View HistoryRow(WatchRequest request)
    {
        Grid row = new()
        {
            ColumnDefinitions = [new ColumnDefinition(44), new ColumnDefinition(GridLength.Star)],
            ColumnSpacing = 10,
            Padding = new Thickness(8, 6),
        };

        row.Add(Poster(request.PosterUrl, 44, 66), 0);
        row.Add(new VerticalStackLayout
        {
            Spacing = 1,
            Children =
            {
                new Label
                {
                    Text = request.Year is int year ? $"{request.Title} ({year})" : request.Title,
                    FontSize = 14,
                    TextColor = Theme.Text,
                },
                new Label
                {
                    // WHO asked is the point of showing this at all. A download
                    // queue says what; a family app says who.
                    Text = request.RequestedBy is string who
                        ? $"{who} · {When(request.RequestedAt)}"
                        : When(request.RequestedAt),
                    FontSize = 11,
                    TextColor = Theme.TextDim,
                },
                new Label
                {
                    Text = request.Outcome,
                    FontSize = 11,
                    TextColor = request.Outcome.StartsWith("Added", StringComparison.Ordinal)
                        ? Theme.Aboard
                        : Theme.TextDim,
                },
            },
        }, 1);

        return row;
    }

    private static View Poster(string? url, int width = 64, int height = 96) =>
        new Image
        {
            Source = string.IsNullOrWhiteSpace(url) ? null : ImageSource.FromUri(new Uri(url)),
            WidthRequest = width,
            HeightRequest = height,
            Aspect = Aspect.AspectFill,
            BackgroundColor = Theme.Ink700,
        };

    private static string When(DateTimeOffset moment)
    {
        TimeSpan ago = DateTimeOffset.Now - moment;
        return ago switch
        {
            { TotalMinutes: < 2 } => "just now",
            { TotalHours: < 1 } => $"{(int)ago.TotalMinutes} min ago",
            { TotalDays: < 1 } => $"{(int)ago.TotalHours}h ago",
            { TotalDays: < 7 } => $"{(int)ago.TotalDays}d ago",
            _ => moment.ToLocalTime().ToString("d MMM"),
        };
    }
}

/// <summary>Start work from an event handler without leaving the task unobserved.</summary>
internal static class TaskExtensions
{
    public static void FireAndForget(this Task task) =>
        _ = task.ContinueWith(
            faulted => System.Diagnostics.Debug.WriteLine(faulted.Exception),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
}
