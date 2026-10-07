// SPDX-License-Identifier: GPL-3.0-or-later

namespace WhenWillTheBus.App.Services;

/// <summary>
/// The device's push token, for ordinary notifications.
/// </summary>
/// <remarks>
/// A DIFFERENT TOKEN FROM THE LIVE ACTIVITY ONE, and they are not
/// interchangeable. An activity token addresses one card and dies with it;
/// this addresses the app and survives until iOS reissues it. Sending either to
/// the wrong topic is a push Apple accepts and drops.
///
/// Held statically because it arrives from the AppDelegate, which nothing
/// constructs and nothing can inject into.
/// </remarks>
public static class PushRegistrar
{
    private const string TokenKey = "wwtb.push.deviceToken";

    /// <summary>
    /// The last token iOS gave us, or null if permission was never granted.
    /// </summary>
    /// <remarks>
    /// Remembered across launches so the worker can be told on the very first
    /// hello, rather than only after iOS gets round to re-issuing it — which
    /// it may not do for days.
    /// </remarks>
    public static string? DeviceToken
    {
        get => Preferences.Get(TokenKey, null as string);
        private set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                Preferences.Remove(TokenKey);
            }
            else
            {
                Preferences.Set(TokenKey, value);
            }
        }
    }

    /// <summary>Raised when the token changes, so it can be sent on straight away.</summary>
    public static event Action<string>? TokenArrived;

    public static void Remember(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token == DeviceToken)
        {
            return;
        }

        DeviceToken = token;
        TokenArrived?.Invoke(token);
    }

    private const string StartTokenKey = "wwtb.push.startToken";

    /// <summary>
    /// This app's PUSH-TO-START token for the bus card, or null before iOS 17.2
    /// or before iOS has issued one.
    /// </summary>
    /// <remarks>
    /// A third token, and not interchangeable with either of the others: it
    /// lets the worker START a card. Remembered across launches for the same
    /// reason as the device token -- iOS does not re-issue it on every run.
    /// </remarks>
    public static string? StartToken
    {
        get => Preferences.Get(StartTokenKey, null as string);
        private set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                Preferences.Remove(StartTokenKey);
            }
            else
            {
                Preferences.Set(StartTokenKey, value);
            }
        }
    }

    /// <summary>Remember a push-to-start token. Returns whether it is new, and so worth sending on.</summary>
    public static bool RememberStartToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token == StartToken)
        {
            return false;
        }

        StartToken = token;
        return true;
    }

    /// <summary>Where a notification asked the app to go, if it did.</summary>
    /// <remarks>
    /// A notification cannot open another app. It opens this one, which then
    /// opens the URL — so "take her to TestFlight" is really "open the app,
    /// which opens TestFlight".
    /// </remarks>
    public static event Action<string>? OpenRequested;

    public static void RequestOpen(string url) => OpenRequested?.Invoke(url);
}
