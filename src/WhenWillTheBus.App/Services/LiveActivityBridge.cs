// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using WhenWillTheBus.Core.Api;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Notifications;

namespace WhenWillTheBus.App.Services;

/// <summary>
/// The C# side of the ActivityKit bridge.
/// </summary>
/// <remarks>
/// WidgetKit UI cannot be written in C#, so the Live Activity's LAYOUT is
/// SwiftUI. Everything that decides WHAT it says — the prediction, the stage
/// machine, the push policy — is C#, and this is the seam between them.
///
/// Starting the activity locally, from the foreground, is the main prize of
/// leaving Home Assistant behind: push-to-start does not work when the app is
/// closed, its token goes stale on Apple's side with no signal, an app update
/// kills it, and every one of those failures is completely silent.
/// </remarks>
public static partial class LiveActivityBridge
{
    /// <summary>Called by Swift whenever the activity's push token changes.</summary>
    private delegate void TokenCallback(IntPtr token);

    private static TokenCallback? _callback;
    private static Action<string>? _onToken;

    [LibraryImport("__Internal")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool wwtb_activities_enabled();

    [LibraryImport("__Internal", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool wwtb_start_activity(string journeyId, string riderName, long childId, string stateJson);

    [LibraryImport("__Internal", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool wwtb_update_activity(string stateJson);

    [LibraryImport("__Internal", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool wwtb_end_activity(string stateJson);

    [LibraryImport("__Internal")]
    private static partial void wwtb_set_token_callback(IntPtr callback);

    [LibraryImport("__Internal")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool wwtb_activity_has_push();

    /// <summary>
    /// Whether the user has Live Activities switched on for this app. A refusal
    /// here is the commonest reason a card never appears.
    /// </summary>
    public static bool Enabled => wwtb_activities_enabled();

    /// <summary>
    /// Whether the running activity can be updated by push.
    /// </summary>
    /// <remarks>
    /// False means the card is local-only: correct while the app is running,
    /// frozen the moment iOS suspends it. That is a real limitation to say out
    /// loud — a countdown that silently stops advancing is worse than one that
    /// admits it cannot.
    /// </remarks>
    public static bool HasPush => wwtb_activity_has_push();

    /// <summary>
    /// Be told when the activity's push token changes, so it can be re-registered
    /// with the worker. IT DOES CHANGE, and a worker pushing to a retired token
    /// fails silently.
    /// </summary>
    public static void OnPushToken(Action<string> handler)
    {
        _onToken = handler;
        _callback = Received;
        wwtb_set_token_callback(Marshal.GetFunctionPointerForDelegate(_callback));

        static void Received(IntPtr token)
        {
            string? hex = Marshal.PtrToStringUTF8(token);
            if (!string.IsNullOrEmpty(hex))
            {
                _onToken?.Invoke(hex);
            }
        }
    }

    public static bool Start(string journeyId, string riderName, long childId, Journey journey, ArrivalPrediction? prediction, RiderInfo? info, DateTimeOffset now) =>
        wwtb_start_activity(journeyId, riderName, childId, Serialise(journey, prediction, info, now));

    public static bool Update(Journey journey, ArrivalPrediction? prediction, RiderInfo? info, DateTimeOffset now) =>
        wwtb_update_activity(Serialise(journey, prediction, info, now));

    public static bool End(Journey journey, ArrivalPrediction? prediction, RiderInfo? info, DateTimeOffset now) =>
        wwtb_end_activity(Serialise(journey, prediction, info, now));

    /// <summary>
    /// The content state, serialised by the SHARED implementation in Core.
    /// </summary>
    /// <remarks>
    /// The phone and the worker both start and update the same card, so this
    /// deliberately does not have its own copy: a divergence would present as a
    /// card that starts fine and then silently ignores every push, because Apple
    /// accepts it and the widget simply cannot decode it.
    /// </remarks>
    private static string Serialise(
        Journey journey,
        ArrivalPrediction? prediction,
        RiderInfo? info,
        DateTimeOffset now) =>
        BusActivityState.For(journey, prediction, info?.DistanceMiles, info?.GpsAgeMinutes, now).ToJson();
}
