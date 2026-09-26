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

    /// <summary>Where a notification asked the app to go, if it did.</summary>
    /// <remarks>
    /// A notification cannot open another app. It opens this one, which then
    /// opens the URL — so "take her to TestFlight" is really "open the app,
    /// which opens TestFlight".
    /// </remarks>
    public static event Action<string>? OpenRequested;

    public static void RequestOpen(string url) => OpenRequested?.Invoke(url);
}
