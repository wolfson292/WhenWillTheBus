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
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        // Four tabs rather than one screen with a Settings button. The bus is
        // what you open the app for, so it stays first and stays uncluttered;
        // the map, the scans and the settings are each a place to go looking.
        TabbedPage tabs = new();
        tabs.Children.Add(Tab<BusPage>());
        tabs.Children.Add(Tab<MapPage>());
        tabs.Children.Add(Tab<ScansPage>());
        tabs.Children.Add(Tab<HistoryPage>());
        tabs.Children.Add(Tab<SignInPage>());

        return new Window(tabs);
    }

    private NavigationPage Tab<TPage>()
        where TPage : Page
    {
        Page page = _services.GetRequiredService<TPage>();
        return new NavigationPage(page) { Title = page.Title };
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
