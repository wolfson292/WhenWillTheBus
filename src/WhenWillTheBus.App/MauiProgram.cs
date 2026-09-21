// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.App.Pages;
using WhenWillTheBus.App.Services;
using WhenWillTheBus.Core;

namespace WhenWillTheBus.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        MauiAppBuilder builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .UseMauiMaps()
            .ConfigureFonts(fonts => fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular"));

        // Our client follows the login's 307 to the account shard itself, so the
        // POST body is visibly replayed rather than left to handler behaviour
        // that varies between platforms.
        builder.Services.AddSingleton(_ => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = Tuning.RequestTimeout,
        });

        builder.Services.AddSingleton<CredentialStore>();
        builder.Services.AddSingleton<ServerLink>();
        builder.Services.AddSingleton<BusService>();
        builder.Services.AddTransient<BusPage>();
        builder.Services.AddTransient<MapPage>();
        builder.Services.AddTransient<ScansPage>();
        builder.Services.AddTransient<HistoryPage>();
        builder.Services.AddTransient<SignInPage>();

        return builder.Build();
    }
}
