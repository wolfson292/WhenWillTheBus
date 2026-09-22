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

        (string Url, string Key)? worker = await _credentials.ReadServerAsync();
        if (worker is not null)
        {
            _serverUrl.Text = worker.Value.Url;
            _serverKey.Text = worker.Value.Key;
        }
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

        await Share.RequestAsync(new ShareTextRequest
        {
            Title = "Set up When Will The Bus",
            Text = SetupLink.Build(worker.Value.Url, worker.Value.Key),
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
