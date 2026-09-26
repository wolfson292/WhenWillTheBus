// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.App.Pages;
using WhenWillTheBus.App.Services;

namespace WhenWillTheBus.App;

public sealed class App : Application
{
    private readonly BusService _bus;
    private readonly IServiceProvider _services;

    public App(BusService bus, IServiceProvider services)
    {
        _bus = bus;
        _services = services;

        Resources = Palette();

        // A setup link only ever OFFERS a configuration. It is confirmed on
        // screen before anything is saved, because a URL scheme can be opened by
        // any app or web page and an arriving link proves nothing about who sent
        // it.
        SetupLink.Received += OnSetupLink;
    }

    private async void OnSetupLink(SetupLink.Details details)
    {
        Page? page = Windows.FirstOrDefault()?.Page;
        if (page is null)
        {
            return;
        }

        bool accepted = await page.DisplayAlertAsync(
            "Use this worker?",
            $"This link points the app at:\n\n{details.Url}\n\nIt also carries an access key. "
            + "Only accept it from someone you trust.",
            "Use it",
            "Cancel");

        if (!accepted)
        {
            return;
        }

        CredentialStore credentials = _services.GetRequiredService<CredentialStore>();
        await credentials.SaveServerAsync(details.Url, details.Key);
        await _bus.StartAsync();

        await page.DisplayAlertAsync(
            "Saved",
            _bus.Problem ?? "Connected to the worker.",
            "OK");
    }

    /// <summary>
    /// The defaults every screen inherits.
    /// </summary>
    /// <remarks>
    /// Implicit styles rather than a colour on each control: an explicit value
    /// set on an instance still wins, so a page that means something particular
    /// keeps saying it, and a page that does not is simply legible. Without
    /// these, MAUI's own defaults put near-black text on the near-black ground
    /// of every screen that is not <see cref="Pages.BusPage"/>.
    /// </remarks>
    private static ResourceDictionary Palette()
    {
        ResourceDictionary resources = new();

        resources.Add(new Style(typeof(Label))
        {
            Setters = { new Setter { Property = Label.TextColorProperty, Value = Theme.Text } },
        });

        resources.Add(new Style(typeof(Entry))
        {
            Setters =
            {
                new Setter { Property = Entry.TextColorProperty, Value = Theme.Text },
                new Setter { Property = Entry.PlaceholderColorProperty, Value = Theme.TextDim },
                new Setter { Property = VisualElement.BackgroundColorProperty, Value = Theme.Ink700 },
            },
        });

        resources.Add(new Style(typeof(Editor))
        {
            Setters =
            {
                new Setter { Property = Editor.TextColorProperty, Value = Theme.Text },
                new Setter { Property = Editor.PlaceholderColorProperty, Value = Theme.TextDim },
                new Setter { Property = VisualElement.BackgroundColorProperty, Value = Theme.Ink700 },
            },
        });

        resources.Add(new Style(typeof(Button))
        {
            Setters =
            {
                new Setter { Property = Button.TextColorProperty, Value = Theme.Ink900 },
                new Setter { Property = VisualElement.BackgroundColorProperty, Value = Theme.Bus },
                new Setter { Property = Button.CornerRadiusProperty, Value = 12 },
            },
        });

        resources.Add(new Style(typeof(ContentPage))
        {
            Setters = { new Setter { Property = VisualElement.BackgroundColorProperty, Value = Theme.Ink900 } },
        });

        resources.Add(new Style(typeof(CollectionView))
        {
            Setters = { new Setter { Property = VisualElement.BackgroundColorProperty, Value = Theme.Ink900 } },
        });

        resources.Add(new Style(typeof(ActivityIndicator))
        {
            Setters = { new Setter { Property = ActivityIndicator.ColorProperty, Value = Theme.Bus } },
        });

        return resources;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        // DARK, WHATEVER THE PHONE IS SET TO. This is read at 6am on a kerb and
        // at 3pm in a car park, and the palette in Theme is built for an unlit
        // ground. Following the system would mean an AppThemeBinding on every
        // property, and half-done that reads as a bug rather than a theme.
        UserAppTheme = AppTheme.Dark;

        // A notification cannot open another app, so "take her to TestFlight"
        // is really "open this app, which opens TestFlight". The URL comes
        // from our own payload and is checked before it is followed: a push is
        // data arriving over the network, and following any URL in one would
        // let whoever can send us a push send the app anywhere.
        PushRegistrar.OpenRequested += url =>
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? destination)
                && destination.Scheme is "itms-beta" or "https")
            {
                MainThread.BeginInvokeOnMainThread(() => Launcher.OpenAsync(destination).FireAndForget());
            }
        };

        // FIVE, AND NOT SIX. iOS collapses a seventh-of-a-tab-bar into a "More"
        // list, which buries whatever lands in it -- so Watch takes the tab
        // that Scans had, and the scans are reached by tapping the line about
        // them on the bus screen, which is where somebody looks for them
        // anyway.
        TabbedPage tabs = new()
        {
            BackgroundColor = Theme.Ink900,
            BarBackgroundColor = Theme.Ink900,
            SelectedTabColor = Theme.Bus,
            UnselectedTabColor = Theme.TextDim,
        };

        tabs.Children.Add(Tab<BusPage>("tab_bus.png"));
        tabs.Children.Add(Tab<MapPage>("tab_map.png"));
        tabs.Children.Add(Tab<WatchPage>("tab_watch.png"));
        tabs.Children.Add(Tab<HistoryPage>("tab_history.png"));
        tabs.Children.Add(Tab<SignInPage>("tab_settings.png"));

        return new Window(tabs);
    }

    /// <summary>
    /// One tab, with its icon.
    /// </summary>
    /// <remarks>
    /// The icon file is named .png even though the source is an SVG: resizetizer
    /// rasterises everything under Resources/Images at build time, and what ends
    /// up in the bundle carries the .png extension. Asking for the .svg finds
    /// nothing, and a missing tab icon is silent — the tab simply renders as its
    /// title, which is exactly what it did before it had one.
    /// </remarks>
    private NavigationPage Tab<TPage>(string icon)
        where TPage : Page
    {
        Page page = _services.GetRequiredService<TPage>();
        page.BackgroundColor = Theme.Ink900;

        return new NavigationPage(page)
        {
            Title = page.Title,
            IconImageSource = ImageSource.FromFile(icon),
            BarBackgroundColor = Theme.Ink900,
            BarTextColor = Theme.Text,
            BackgroundColor = Theme.Ink900,
        };
    }

    /// <summary>
    /// Stop polling when the app leaves the foreground.
    /// </summary>
    /// <remarks>
    /// iOS will suspend this process within seconds regardless, and the worker
    /// takes over. Pretending otherwise burns battery and hammers somebody
    /// else's service for readings nobody is looking at.
    /// </remarks>
    protected override void OnSleep() => _bus.Pause();

    protected override async void OnResume() => await _bus.StartAsync();
}
