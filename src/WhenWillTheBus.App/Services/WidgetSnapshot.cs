// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using Foundation;
using WhenWillTheBus.Core.Api;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Notifications;

namespace WhenWillTheBus.App.Services;

/// <summary>
/// Leaves the next arrival where the home-screen widget can find it.
/// </summary>
/// <remarks>
/// A widget cannot run the prediction: it wakes for a moment, draws, and goes
/// away. It has no history and no engine. So the app writes a snapshot into
/// the shared App Group container after each poll, and the widget reads it --
/// or, when the app has not run for a while, asks the worker for the same
/// thing. See <see cref="WriteWorkerLink"/>.
///
/// EVERY FIELD HERE MUST MATCH <c>BusSnapshot</c> in
/// ios/LiveActivity/BusSnapshot.swift. Two processes, two languages, and a
/// mismatch is a widget stuck on its placeholder with nothing explaining why.
/// </remarks>
public static partial class WidgetSnapshot
{
    /// <summary>Must match <c>BusSnapshot.appGroup</c> and both entitlements files.</summary>
    private const string AppGroup = "group.com.denlair.whenwillthebus";
    private const string Filename = "widget-snapshot.json";

    [LibraryImport("__Internal")]
    private static partial void wwtb_reload_widgets();

    /// <summary>Write the snapshot and ask WidgetKit to redraw.</summary>
    /// <remarks>
    /// Never throws. A widget that cannot be updated is a stale number on a home
    /// screen; an exception here would take down the poll that feeds everything
    /// else.
    /// </remarks>
    public static void Write(
        Student? rider,
        Journey journey,
        ArrivalPrediction? prediction,
        RiderInfo? info,
        DateTimeOffset now)
    {
        try
        {
            NSUrl? container = NSFileManager.DefaultManager.GetContainerUrl(AppGroup);
            if (container is null)
            {
                // The App Group is not configured on this build. The app works
                // fine; only the widget goes without.
                return;
            }

            string path = Path.Combine(container.Path!, Filename);
            File.WriteAllText(path, Serialise(rider, journey, prediction, info, now));

            wwtb_reload_widgets();
        }
        catch (Exception)
        {
            // Deliberately swallowed. See above.
        }
    }

    /// <summary>The shared serialiser in Core, so the worker's /widget says exactly the same.</summary>
    private const string WorkerLinkFilename = "widget-worker.json";

    /// <summary>
    /// Tell the widget where the worker is, so it can fetch for itself.
    /// </summary>
    /// <remarks>
    /// The app only writes a snapshot while it is running, and on a school day
    /// nobody runs it: on 7 Oct the widget had no status all afternoon while
    /// the worker knew the rider was aboard and when she would be home.
    ///
    /// THIS PUTS THE WORKER KEY IN THE APP GROUP, which only this app and its
    /// own widget can read. It is the same key the app already holds, on the
    /// same phone; the widget needs it to authenticate, and an extension cannot
    /// read the app's Keychain items without a shared access group. Removed
    /// when no worker is configured, so signing out takes it away too.
    /// </remarks>
    public static void WriteWorkerLink((string Url, string Key)? worker)
    {
        try
        {
            NSUrl? container = NSFileManager.DefaultManager.GetContainerUrl(AppGroup);
            if (container is null)
            {
                return;
            }

            string path = Path.Combine(container.Path!, WorkerLinkFilename);
            if (worker is null)
            {
                File.Delete(path);
                return;
            }

            string json = System.Text.Json.JsonSerializer.Serialize(
                new Dictionary<string, string> { ["url"] = worker.Value.Url, ["key"] = worker.Value.Key });

            // Readable after the first unlock, which is all a widget refreshing
            // on a locked phone needs, and no more.
            File.WriteAllText(path, json);
            NSFileManager.DefaultManager.SetAttributes(
                new NSFileAttributes { ProtectionKey = NSFileProtection.CompleteUntilFirstUserAuthentication },
                path,
                out _);

            wwtb_reload_widgets();
        }
        catch (Exception)
        {
            // Deliberately swallowed: the widget falls back to the app's file.
        }
    }

    private static string Serialise(
        Student? rider,
        Journey journey,
        ArrivalPrediction? prediction,
        RiderInfo? info,
        DateTimeOffset now) =>
        HomeSnapshot.Serialise(rider?.Name, rider?.BusNumber, journey, prediction, info?.DistanceMiles, now);
}
