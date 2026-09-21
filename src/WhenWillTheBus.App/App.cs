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

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new NavigationPage(_services.GetRequiredService<BusPage>()));

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
