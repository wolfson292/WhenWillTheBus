// SPDX-License-Identifier: GPL-3.0-or-later

using Foundation;
using UIKit;
using UserNotifications;
using WhenWillTheBus.App.Services;

namespace WhenWillTheBus.App;

[Register(nameof(AppDelegate))]
public class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    /// <summary>
    /// Ask for notification permission and register for a device token.
    /// </summary>
    /// <remarks>
    /// SEPARATE FROM THE LIVE ACTIVITY PERMISSION. A Live Activity needs no
    /// authorisation prompt and works without this; an ordinary notification
    /// needs one, and until somebody says yes there is no device token and the
    /// worker cannot reach the phone at all.
    ///
    /// Asked at launch rather than at the moment it is first needed, because
    /// the moment it is first needed is somebody else deciding to send it.
    /// </remarks>
    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        bool started = base.FinishedLaunching(application, launchOptions!);

        UNUserNotificationCenter.Current.Delegate = new NotificationDelegate();
        UNUserNotificationCenter.Current.RequestAuthorization(
            UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound,
            (granted, _) =>
            {
                if (!granted)
                {
                    // Declined. Everything else still works; only being told
                    // things does not.
                    return;
                }

                // MUST happen on the main thread, and iOS answers by calling
                // RegisteredForRemoteNotifications below.
                MainThread.BeginInvokeOnMainThread(
                    () => UIApplication.SharedApplication.RegisterForRemoteNotifications());
            });

        return started;
    }

    /// <summary>
    /// iOS handing back the device token.
    /// </summary>
    /// <remarks>
    /// Exported by SELECTOR rather than overridden: MauiUIApplicationDelegate
    /// does not declare these two as virtual, so an `override` does not
    /// compile and a plain method is never called. Naming the Objective-C
    /// selector is what actually wires it up.
    /// </remarks>
    [Export("application:didRegisterForRemoteNotificationsWithDeviceToken:")]
    public void RegisteredForRemoteNotifications(UIApplication application, NSData deviceToken)
    {
        // APNs wants it as hex. NSData.Description used to be the shortcut for
        // this and stopped being one: on iOS 13 it became "{length = 32, bytes
        // = 0x...}", which registers cleanly and then never receives anything.
        byte[] bytes = new byte[deviceToken.Length];
        System.Runtime.InteropServices.Marshal.Copy(deviceToken.Bytes, bytes, 0, (int)deviceToken.Length);

        PushRegistrar.Remember(Convert.ToHexString(bytes).ToLowerInvariant());
    }

    [Export("application:didFailToRegisterForRemoteNotificationsWithError:")]
    public void FailedToRegisterForRemoteNotifications(UIApplication application, NSError error) =>
        System.Diagnostics.Debug.WriteLine($"Push registration failed: {error.LocalizedDescription}");

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

/// <summary>What happens when somebody taps a notification.</summary>
/// <remarks>
/// The URL is taken from OUR payload and is checked before it is followed.
/// A push is data arriving over the network; treating the URL in one as an
/// instruction would let anyone who could send us a push send the app
/// anywhere.
/// </remarks>
internal sealed class NotificationDelegate : UNUserNotificationCenterDelegate
{
    public override void DidReceiveNotificationResponse(
        UNUserNotificationCenter center, UNNotificationResponse response, Action completionHandler)
    {
        if (response.Notification.Request.Content.UserInfo["openUrl"] is NSString url)
        {
            PushRegistrar.RequestOpen(url.ToString());
        }

        completionHandler();
    }

    /// <summary>Show it even with the app open, rather than swallowing it silently.</summary>
    public override void WillPresentNotification(
        UNUserNotificationCenter center, UNNotification notification, Action<UNNotificationPresentationOptions> completionHandler) =>
        completionHandler(UNNotificationPresentationOptions.Banner | UNNotificationPresentationOptions.Sound);
}
