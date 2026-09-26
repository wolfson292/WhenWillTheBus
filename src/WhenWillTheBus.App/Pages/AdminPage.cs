// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.App.Services;

namespace WhenWillTheBus.App.Pages;

/// <summary>
/// The one screen only the admin key opens.
/// </summary>
/// <remarks>
/// Reached from Settings rather than given a tab, because the tab bar holds
/// five and a sixth becomes an iOS "More" list. It is also not a screen anybody
/// needs often.
///
/// Every section here fails soft and independently. MagicMovieNight being down
/// must not take out the phone list, and a phone that has refused notifications
/// must not make the rest of the list unusable — so each part says what it
/// knows and nothing pretends the others are broken.
/// </remarks>
public sealed class AdminPage : ContentPage
{
    private readonly ServerLink _server;

    private readonly VerticalStackLayout _phones = new() { Spacing = 10 };
    private readonly VerticalStackLayout _tonight = new() { Spacing = 8 };

    private readonly Picker _recipient = new()
    {
        Title = "Who to send it to",
        TextColor = Theme.Text,
        TitleColor = Theme.TextDim,
    };

    private readonly Entry _title = new() { Placeholder = "Title", Text = "Wolf Family" };
    private readonly Entry _body = new() { Placeholder = "What do you want to say?" };
    private readonly Label _sent = new() { FontSize = 12, TextColor = Theme.TextDim };

    private IReadOnlyList<FamilyPhone> _known = [];

    public AdminPage(ServerLink server)
    {
        _server = server;
        Title = "Admin";
        BackgroundColor = Theme.Ink900;
        Padding = new Thickness(16, 12);

        Button send = new() { Text = "Send notification", FontSize = 14 };
        send.Clicked += async (_, _) => await SendAsync(send);

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Spacing = 6,
                Children =
                {
                    Heading("Registered phones"),
                    Note("Every phone that has opened the app, and whether the worker can reach it."),
                    _phones,

                    Heading("Send a notification"),
                    Note("Goes straight to that phone. It opens the app when tapped."),
                    _recipient,
                    _title,
                    _body,
                    send,
                    _sent,

                    Heading("What to watch tonight"),
                    Note("From MagicMovieNight, which already knows what the household watches."),
                    _tonight,
                },
            },
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        RefreshAsync().FireAndForget();
    }

    private async Task RefreshAsync()
    {
        _known = await _server.PhonesAsync();

        _phones.Clear();
        if (_known.Count == 0)
        {
            _phones.Add(Dim("No phone has introduced itself yet."));
        }

        foreach (FamilyPhone phone in _known)
        {
            _phones.Add(PhoneRow(phone));
        }

        _recipient.ItemsSource = _known
            .Select(phone => phone.Label ?? $"unnamed ({phone.Model})")
            .ToList();

        if (_known.Count > 0)
        {
            _recipient.SelectedIndex = 0;
        }

        _tonight.Clear();
        IReadOnlyList<TonightPick> picks = await _server.TonightAsync();
        if (picks.Count == 0)
        {
            _tonight.Add(Dim("Nothing suggested yet, or MagicMovieNight is not reachable."));
            return;
        }

        foreach (TonightPick pick in picks)
        {
            _tonight.Add(PickRow(pick));
        }
    }

    /// <summary>Where a phone is sent to install an update.</summary>
    /// <remarks>
    /// A notification cannot open another app; it opens this one, which opens
    /// this. itms-beta:// is TestFlight's own scheme, and the number is the App
    /// Store Connect app id.
    /// </remarks>
    private const string TestFlightUrl = "itms-beta://beta.itunes.apple.com/v1/app/6814865915";

    private View PhoneRow(FamilyPhone phone)
    {
        List<string> facts = [phone.Model ?? "unknown model", phone.Sandbox ? "development" : "TestFlight"];
        if (phone.Build is string build)
        {
            facts.Add(phone.UpdateAvailable ? $"on {build} — update waiting" : $"on {build}");
        }

        // Stated rather than implied. A phone that has refused notifications is
        // silently unreachable otherwise, and the sending looks broken instead.
        facts.Add(phone.Reachable ? "notifications on" : "notifications OFF");

        Label detail = new()
        {
            Text = string.Join(" · ", facts),
            FontSize = 11,
            TextColor = phone.UpdateAvailable ? Theme.Bus
                : phone.Reachable ? Theme.TextDim : Theme.Fault,
        };

        // Offered ONLY when there is actually something to install. A button
        // that sends somebody to TestFlight to find nothing new is worse than
        // no button.
        Button remind = new()
        {
            Text = "Remind to update",
            FontSize = 12,
            Padding = new Thickness(10, 3),
            HorizontalOptions = LayoutOptions.Start,
            Margin = new Thickness(0, 6, 0, 0),
            IsVisible = phone.UpdateAvailable && phone.Reachable,
        };

        remind.Clicked += async (_, _) =>
        {
            remind.IsEnabled = false;
            detail.Text = await _server.NudgeAsync(
                phone.Id,
                "Update available",
                "A new version of Wolf Family is ready in TestFlight.",
                TestFlightUrl);
            remind.IsEnabled = true;
        };

        return new Border
        {
            Padding = new Thickness(12, 10),
            BackgroundColor = Theme.Ink800,
            Stroke = Theme.Ink700,
            StrokeThickness = 1,
            Content = new VerticalStackLayout
            {
                Spacing = 1,
                Children =
                {
                    new Label
                    {
                        Text = phone.Label ?? "unnamed",
                        FontSize = 15,
                        FontAttributes = FontAttributes.Bold,
                        TextColor = Theme.Text,
                    },
                    detail,
                    remind,
                },
            },
        };
    }

    private static View PickRow(TonightPick pick)
    {
        Grid row = new()
        {
            ColumnDefinitions = [new ColumnDefinition(52), new ColumnDefinition(GridLength.Star)],
            ColumnSpacing = 10,
        };

        row.Add(new Image
        {
            Source = string.IsNullOrWhiteSpace(pick.PosterUrl)
                ? null
                : ImageSource.FromUri(new Uri(pick.PosterUrl)),
            WidthRequest = 52,
            HeightRequest = 78,
            Aspect = Aspect.AspectFill,
            BackgroundColor = Theme.Ink700,
        }, 0);

        List<string> where = [];
        if (pick.InLibrary)
        {
            where.Add("in the library");
        }
        else if (pick.WhereToWatch is string service)
        {
            where.Add(service);
        }

        row.Add(new VerticalStackLayout
        {
            Spacing = 1,
            Children =
            {
                new Label
                {
                    Text = pick.Year is int year ? $"{pick.Rank}. {pick.Title} ({year})" : $"{pick.Rank}. {pick.Title}",
                    FontSize = 14,
                    FontAttributes = FontAttributes.Bold,
                    TextColor = Theme.Text,
                },
                new Label
                {
                    Text = string.Join(" · ", where.Prepend(pick.Kind)),
                    FontSize = 11,
                    TextColor = pick.InLibrary ? Theme.Aboard : Theme.TextDim,
                },
                new Label
                {
                    Text = pick.Pitch ?? string.Empty,
                    FontSize = 12,
                    TextColor = Theme.TextMid,
                    MaxLines = 3,
                    LineBreakMode = LineBreakMode.TailTruncation,
                    IsVisible = !string.IsNullOrWhiteSpace(pick.Pitch),
                },
            },
        }, 1);

        return row;
    }

    private async Task SendAsync(Button send)
    {
        if (_recipient.SelectedIndex < 0 || _recipient.SelectedIndex >= _known.Count)
        {
            _sent.Text = "Pick who it is going to first.";
            return;
        }

        FamilyPhone target = _known[_recipient.SelectedIndex];
        if (!target.Reachable)
        {
            // Refused here rather than at the worker, so the reason is the
            // phone's settings and not a failure somewhere in the middle.
            _sent.Text = $"{target.Label ?? "That phone"} has not allowed notifications.";
            return;
        }

        string body = _body.Text?.Trim() ?? string.Empty;
        if (body.Length == 0)
        {
            _sent.Text = "Say something first.";
            return;
        }

        send.IsEnabled = false;
        _sent.Text = "Sending…";

        _sent.Text = await _server.NudgeAsync(
            target.Id,
            string.IsNullOrWhiteSpace(_title.Text) ? "Wolf Family" : _title.Text.Trim(),
            body);

        _body.Text = string.Empty;
        send.IsEnabled = true;
    }

    private static Label Heading(string text) => new()
    {
        Text = text,
        FontSize = 13,
        FontAttributes = FontAttributes.Bold,
        TextColor = Theme.TextDim,
        Margin = new Thickness(0, 18, 0, 2),
    };

    private static Label Note(string text) =>
        new() { Text = text, FontSize = 12, TextColor = Theme.TextDim };

    private static Label Dim(string text) =>
        new() { Text = text, FontSize = 13, TextColor = Theme.TextDim };
}
