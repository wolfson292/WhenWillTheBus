// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.App.Services;
using WhenWillTheBus.Core.Api;

namespace WhenWillTheBus.App.Pages;

/// <summary>
/// The account, the worker, and importing history.
/// </summary>
/// <remarks>
/// The password goes straight to the Keychain via <see cref="CredentialStore"/>
/// and is never held anywhere else.
/// </remarks>
public sealed class SignInPage : ContentPage
{
    private readonly CredentialStore _credentials;
    private readonly ServerLink _server;
    private readonly BusService _bus;

    private readonly Entry _email = new() { Placeholder = "Email", Keyboard = Keyboard.Email };
    private readonly Entry _password = new() { Placeholder = "Password", IsPassword = true };
    private readonly Entry _serverUrl = new() { Placeholder = "http://homeserver.local:8080", Keyboard = Keyboard.Url };
    private readonly Entry _serverKey = new() { Placeholder = "Worker API key", IsPassword = true };
    private readonly Label _status = new() { FontSize = 13, TextColor = Colors.Gray };

    public SignInPage(CredentialStore credentials, ServerLink server, BusService bus)
    {
        _credentials = credentials;
        _server = server;
        _bus = bus;

        Title = "Settings";
        Padding = new Thickness(20, 16);

        Button save = new() { Text = "Save and sign in" };
        save.Clicked += OnSave;

        Button import = new() { Text = "Import history from Home Assistant export" };
        import.Clicked += OnImport;

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Spacing = 12,
                Children =
                {
                    Heading("WheresTheBus account"),
                    _email,
                    _password,

                    Heading("Always-on worker (optional)"),
                    Note(
                        "Without a worker the app only updates while it is open. iOS suspends it "
                        + "within seconds of the phone going in a pocket, which is most of the "
                        + "twenty minutes that actually matter."),
                    _serverUrl,
                    _serverKey,

                    Heading("History"),
                    Note(
                        "Predictions work from two route samples, so a fortnight of history makes "
                        + "this useful on day one rather than in October. The export stays on this "
                        + "device: it contains a child's real route."),
                    import,

                    new BoxView { HeightRequest = 8, Color = Colors.Transparent },
                    save,
                    _status,
                },
            },
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        WheresTheBusCredentials? existing = await _credentials.ReadAsync();
        if (existing is not null)
        {
            _email.Text = existing.Email;
        }

        (string Url, string Key)? server = await _credentials.ReadServerAsync();
        if (server is not null)
        {
            _serverUrl.Text = server.Value.Url;
            _serverKey.Text = server.Value.Key;
        }
    }

    private async void OnSave(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_email.Text) || string.IsNullOrWhiteSpace(_password.Text))
        {
            _status.Text = "Email and password are both needed.";
            return;
        }

        await _credentials.SaveAsync(_email.Text.Trim(), _password.Text);

        if (!string.IsNullOrWhiteSpace(_serverUrl.Text) && !string.IsNullOrWhiteSpace(_serverKey.Text))
        {
            bool reachable = await _server.CheckAsync(_serverUrl.Text.Trim(), _serverKey.Text);
            await _credentials.SaveServerAsync(_serverUrl.Text.Trim(), _serverKey.Text);

            _status.Text = reachable
                ? "Saved. The worker answered."
                : "Saved, but the worker did not answer. The app still works while it is open.";
        }
        else
        {
            _status.Text = "Saved.";
        }

        await _bus.StartAsync();
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
            int imported = await _bus.ImportHistoryAsync(await reader.ReadToEndAsync());

            _status.Text = imported > 0
                ? $"Imported {imported} arrival(s)."
                : "That file had no arrivals in it.";
        }
        catch (InvalidDataException error)
        {
            _status.Text = $"That is not a WheresTheBus export: {error.Message}";
        }
    }

    private static Label Heading(string text) =>
        new() { Text = text, FontSize = 17, FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 10, 0, 0) };

    private static Label Note(string text) =>
        new() { Text = text, FontSize = 12, TextColor = Colors.Gray };
}
