// SPDX-License-Identifier: GPL-3.0-or-later

using Foundation;
using UIKit;
using WhenWillTheBus.App.Services;

namespace WhenWillTheBus.App;

[Register(nameof(AppDelegate))]
public class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    /// <summary>
    /// Handle a setup link: whenwillthebus://setup?url=...&amp;key=...
    /// </summary>
    /// <remarks>
    /// Parsed here and handed on; NOTHING is saved without the user agreeing to
    /// it on screen first. A URL scheme can be opened by any app or any web
    /// page, so a link is a suggestion, never an instruction.
    /// </remarks>
    public override bool OpenUrl(UIApplication application, NSUrl url, NSDictionary options)
    {
        if (SetupLink.TryParse(url?.AbsoluteString, out SetupLink.Details details))
        {
            SetupLink.Offer(details);
            return true;
        }

        return base.OpenUrl(application, url!, options);
    }
}
