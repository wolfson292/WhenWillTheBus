// SPDX-License-Identifier: GPL-3.0-or-later

using Foundation;

namespace WhenWillTheBus.App;

[Register(nameof(AppDelegate))]
public class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
