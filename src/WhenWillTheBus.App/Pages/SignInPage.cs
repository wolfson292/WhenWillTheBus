// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.App.Services;
using WhenWillTheBus.Core.Api;

namespace WhenWillTheBus.App.Pages;

/// <summary>
/// The account, the worker, history, and a way to test the Lock Screen card.
/// </summary>
/// <remarks>
/// EACH SECTION SAVES ITSELF. A single Save button covering both the account and
/// the worker meant changing the worker's address demanded the WheresTheBus
/// password again -- and the password field is deliberately never repopulated,
/// because redisplaying a stored password is worse than retyping one. So the
/// combined button quietly made the safe choice expensive.
/// </remarks>
public sealed class SignInPage : ContentPage
{
    private readonly CredentialStore _credentials;
    private readonly ServerLink _server;
    private readonly BusService _bus;

    private readonly Entry _email = new() { Placeholder = "Email", Keyboard = Keyboard.Email };
    private readonly Entry _password = new() { Placeholder = "Password", IsPassword = true };
    private readonly Entry _serverUrl = new() { Placeholder = "http://192.168.1.10:8471", Keyboard = Keyboard.Url };
    private readonly Entry _serverKey = new() { Placeholder = "Worker API key", IsPassword = true };
    private readonly Entry _deviceLabel = new() { Placeholder = "This phone's name, e.g. Angela's iPhone" };
    private readonly Label _status = new() { FontSize = 13, TextColor = Theme.TextDim };

    /// <summary>Where a phone is sent to install an update.</summary>
    /// <remarks>
    /// A notification cannot open another app; it opens this one, which opens
    /// this. itms-beta:// is TestFlight's own scheme, and the number is the App
    /// Store Connect app id.
    /// </remarks>
    private const string TestFlightUrl = "itms-beta://beta.itunes.apple.com/v1/app/6814865915";

    // Hidden unless the worker says this key is an admin one. Not merely
    // disabled: offering a control that always refuses is worse than not
    // offering it at all.
    private readonly Label _adminHeading = new()
    {
        Text = "Family phones",
        FontSize = 13,
        FontAttributes = FontAttributes.Bold,
        TextColor = Theme.TextDim,
        Margin = new Thickness(0, 18, 0, 2),
        IsVisible = false,
    };

    private readonly Label _adminNote = new()
    {
        Text = "Every phone that has opened the app. Nudge one to remind them to update.",
        FontSize = 12,
        TextColor = Theme.TextDim,
        IsVisible = false,
    };

    private readonly VerticalStackLayout _phones = new() { Spacing = 8, IsVisible = false };

    public SignInPage(CredentialStore credentials, ServerLink server, BusService bus)
    {
        _credentials = credentials;
        _server = server;
        _bus = bus;

        Title = "Settings";
        Padding = new Thickness(20, 16);

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Spacing = 10,
                Children =
                {
                    Heading("WheresTheBus account"),
                    _email,
                    _password,
                    Note("The password is never shown back to you, so this field starts empty "
                        + "even when one is saved. Fill both in only when changing the account."),
                    Action("Save account", OnSaveAccount),

                    Heading("Always-on worker"),
                    Note("Optional, but without it the app only updates while it is open: iOS "
                        + "suspends it within seconds of the phone going in a pocket, which is "
                        + "most of the twenty minutes that actually matter."),
                    _serverUrl,
                    _serverKey,
                    Action("Save worker", OnSaveWorker),
                    Action("Share a setup link", OnShareSetup),
                    Note("Sends a link that configures another phone in one tap. It carries the "
                        + "access key, so send it the way you would send a password."),

                    _adminHeading,
                    _adminNote,
                    _phones,

                    Heading("This phone"),
                    Note("A name for the worker's status page, so its list of connected phones "
                        + "reads as people rather than identifiers. iOS stopped letting apps "
                        + "read the device name in iOS 16, so it can only come from here. The "
                        + "phone also sends its model, its iOS version and the app version — "
                        + "nothing that identifies a person, and never its location."),
                    _deviceLabel,
                    Action("Save this phone's name", OnSaveLabel),

                    Heading("History"),
                    Note("Predictions work from two route samples, so a fortnight of history is "
                        + "the difference between a useful estimate today and one in October. "
                        + "The worker watches every run; this phone only learns while the app is "
                        + "open, so sync now and then or its estimates fall behind."),
                    Action("Sync history from the worker", OnSync),
                    Action("Import a Home Assistant export", OnImport),

                    Heading("Test the Lock Screen card"),
                    Note("Starts a real Live Activity with made-up numbers, so the push path can "
                        + "be proved without waiting for a school run. Lock the phone afterwards, "
                        + "then have the worker push to it."),
                    Action("Start a test card", OnTestCard),
                    Action("End the test card", OnEndCard),

                    new BoxView { HeightRequest = 12, Color = Colors.Transparent },
                    _status,
                },
            },
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        WheresTheBusCredentials? account = await _credentials.ReadAsync();
        if (account is not null)
        {
            _email.Text = account.Email;
        }

        _deviceLabel.Text = DeviceIdentity.Label;
        ShowAdminAsync().FireAndForget();

        (string Url, string Key)? worker = await _credentials.ReadServerAsync();
        if (worker is not null)
        {
            _serverUrl.Text = worker.Value.Url;
            _serverKey.Text = worker.Value.Key;
        }
    }

    /// <summary>Show the admin section, if the worker says this phone is one.</summary>
    private async Task ShowAdminAsync()
    {
        WorkerRole role = await _server.RoleAsync();

        if (role.UpdateAvailable)
        {
            // Belt as well as braces. The push is how this is normally heard,
            // and one swiped away -- or never permitted -- would otherwise
            // leave no trace anywhere in the app.
            _status.Text = "An update is waiting in TestFlight.";
        }

        _adminHeading.IsVisible = _adminNote.IsVisible = _phones.IsVisible = role.IsAdmin;
        if (!role.IsAdmin)
        {
            return;
        }

        _phones.Clear();
        foreach (FamilyPhone phone in await _server.PhonesAsync())
        {
            _phones.Add(PhoneRow(phone));
        }
    }

    private View PhoneRow(FamilyPhone phone)
    {
        Label detail = new()
        {
            Text = Describe(phone),
            FontSize = 11,
            TextColor = phone.UpdateAvailable ? Theme.Bus : Theme.TextDim,
        };

        Button nudge = new()
        {
            Text = phone.UpdateAvailable ? "Remind" : "Nudge",
            FontSize = 12,
            Padding = new Thickness(10, 4),

            // Disabled rather than hidden, with the reason in the line above:
            // a phone that has refused notifications is a fact worth seeing.
            IsEnabled = phone.Reachable,
        };

        nudge.Clicked += async (_, _) =>
        {
            nudge.IsEnabled = false;
            nudge.Text = "Sending…";

            (string title, string body) = phone.UpdateAvailable
                ? ("Update available", "A new version of Wolf Family is ready in TestFlight.")
                : ("Wolf Family", "Open the app when you get a moment.");

            // Somewhere to go only when there IS somewhere to go. A plain nudge
            // that dropped somebody into TestFlight for no reason would be
            // worse than no nudge.
            string? openUrl = phone.UpdateAvailable ? TestFlightUrl : null;

            detail.Text = await _server.NudgeAsync(phone.Id, title, body, openUrl);
            nudge.Text = phone.UpdateAvailable ? "Remind" : "Nudge";
            nudge.IsEnabled = phone.Reachable;
        };

        Grid row = new()
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
            ColumnSpacing = 8,
        };

        row.Add(new VerticalStackLayout
        {
            Spacing = 0,
            Children =
            {
                new Label { Text = phone.Label ?? "unnamed", FontSize = 14, TextColor = Theme.Text },
                detail,
            },
        }, 0);
        row.Add(nudge, 1);
        return row;
    }

    private static string Describe(FamilyPhone phone)
    {
        List<string> parts = [phone.Model ?? "unknown model", phone.Sandbox ? "development" : "TestFlight"];

        if (phone.UpdateAvailable)
        {
            parts.Add($"on {phone.Build} — update waiting");
        }
        else if (phone.Build is string build)
        {
            parts.Add($"on {build}");
        }

        if (!phone.Reachable)
        {
            parts.Add("notifications not allowed");
        }

        return string.Join(" · ", parts);
    }

    private async void OnSaveLabel(object? sender, EventArgs e)
    {
        DeviceIdentity.Label = _deviceLabel.Text;

        // Sent straight away rather than at the next heartbeat. Somebody who
        // has just typed a name is about to go and look for it.
        bool reached = await _server.HelloAsync();
        _status.Text = reached
            ? $"This phone is now \"{DeviceIdentity.Label}\" on the worker."
            : "Saved on this phone. The worker could not be reached, so it will "
              + "pick the name up at the next check-in.";
    }

    private async void OnSaveAccount(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_email.Text) || string.IsNullOrWhiteSpace(_password.Text))
        {
            _status.Text = "Email and password are both needed to change the account.";
            return;
        }

        await _credentials.SaveAsync(_email.Text.Trim(), _password.Text);
        _password.Text = string.Empty;
        _status.Text = "Account saved. Signing in...";
        await _bus.StartAsync();
        _status.Text = _bus.Problem ?? "Account saved and signed in.";
    }

    private async void OnSaveWorker(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_serverUrl.Text) || string.IsNullOrWhiteSpace(_serverKey.Text))
        {
            _status.Text = "The worker needs both an address and a key.";
            return;
        }

        string url = _serverUrl.Text.Trim();
        string key = _serverKey.Text;

        _status.Text = "Checking the worker...";
        bool reachable = await _server.CheckAsync(url, key);
        await _credentials.SaveServerAsync(url, key);

        _status.Text = reachable
            ? "Worker saved, and it answered."
            : "Worker saved, but it did not answer. Check the address and key — the app still "
                + "works while it is open.";
    }

    private async void OnShareSetup(object? sender, EventArgs e)
    {
        (string Url, string Key)? worker = await _credentials.ReadServerAsync();
        if (worker is null)
        {
            _status.Text = "Save a worker first — there is nothing to share yet.";
            return;
        }

        // NOT necessarily the key this phone holds. An admin's phone holds the
        // admin key, and sharing that would make the recipient an admin
        // without either of them being told.
        string? key = await _server.ShareableKeyAsync();
        if (key is null)
        {
            _status.Text = "Could not work out which key to share.";
            return;
        }

        await Share.RequestAsync(new ShareTextRequest
        {
            Title = "Set up Wolf Family",
            Text = SetupLink.Build(worker.Value.Url, key),
        });
    }

    private async void OnSync(object? sender, EventArgs e)
    {
        _status.Text = "Asking the worker...";
        int held = await _bus.SyncHistoryFromWorkerAsync();

        _status.Text = held switch
        {
            -1 => "Could not reach the worker. Check its address and key.",
            0 => "The worker has not learned anything yet.",
            _ => $"Synced. {held} arrival(s) held.",
        };
    }

    private async void OnImport(object? sender, EventArgs e)
    {
        try
        {
            FileResult? file = await FilePicker.PickAsync(new PickOptions { PickerTitle = "Choose the export" });
            if (file is null)
            {
                return;
            }

            using Stream stream = await file.OpenReadAsync();
            using StreamReader reader = new(stream);
            int held = await _bus.ImportHistoryAsync(await reader.ReadToEndAsync());

            _status.Text = held > 0 ? $"Imported. {held} arrival(s) held." : "That file had no arrivals in it.";
        }
        catch (InvalidDataException error)
        {
            _status.Text = $"That is not a WheresTheBus export: {error.Message}";
        }
    }

    private async void OnTestCard(object? sender, EventArgs e)
    {
        _status.Text = "Starting a card...";
        _status.Text = await _bus.StartTestCardAsync();
    }

    private void OnEndCard(object? sender, EventArgs e) => _status.Text = _bus.EndTestCard();

    private static Button Action(string text, EventHandler handler)
    {
        Button button = new() { Text = text };
        button.Clicked += handler;
        return button;
    }

    private static Label Heading(string text) =>
        new() { Text = text, FontSize = 17, FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 12, 0, 0) };

    private static Label Note(string text) =>
        new() { Text = text, FontSize = 12, TextColor = Theme.TextDim };
}
