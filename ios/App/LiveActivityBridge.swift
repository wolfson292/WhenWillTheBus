// SPDX-License-Identifier: GPL-3.0-or-later
//
// The whole of the Swift that the APP (as opposed to the widget) needs.
//
// WidgetKit UI cannot be written in C#, but driving ActivityKit is just a few
// calls, so this file exposes them as C entry points and the C# side calls them
// with DllImport("__Internal"). Everything above this line -- the prediction,
// the stage machine, the push policy -- is C#.
//
// BusActivityAttributes.swift must be a member of BOTH the app target and the
// widget extension target: the two processes exchange this type, and a copy
// that differs in either is a Live Activity that starts and then ignores every
// update.

import ActivityKit
import Foundation

private var liveActivity: Any?
private var tokenCallback: (@convention(c) (UnsafePointer<CChar>?) -> Void)?
private var pushAvailable = false

/// Whether the user has left Live Activities switched on for this app.
///
/// Worth asking before trying: a refusal here is the most common reason a card
/// never appears, and it is a setting the user can toggle at any time.
@_cdecl("wwtb_activities_enabled")
public func wwtb_activities_enabled() -> Bool {
    if #available(iOS 16.2, *) {
        return ActivityAuthorizationInfo().areActivitiesEnabled
    }
    return false
}

/// Register a callback to receive this activity's push token, as hex.
///
/// The token is for the ACTIVITY, not the device, and it changes. Whenever it
/// does, the C# side re-registers it with the worker — otherwise the worker
/// keeps pushing to a token Apple has already retired, silently.
@_cdecl("wwtb_set_token_callback")
public func wwtb_set_token_callback(_ callback: @escaping @convention(c) (UnsafePointer<CChar>?) -> Void) {
    tokenCallback = callback
}

/// Start a Live Activity for one journey, locally.
///
/// STARTING LOCALLY IS THE POINT OF THE REWRITE. Push-to-start does not work
/// when the app is closed, its token goes stale on Apple's side with no signal,
/// an app update kills it, and every one of those failures is completely silent.
/// An activity started here and updated by push is a far more reliable path.
@_cdecl("wwtb_start_activity")
public func wwtb_start_activity(
    _ journeyId: UnsafePointer<CChar>,
    _ riderName: UnsafePointer<CChar>,
    _ childId: Int64,
    _ stateJson: UnsafePointer<CChar>
) -> Bool {
    guard #available(iOS 16.2, *) else { return false }
    guard ActivityAuthorizationInfo().areActivitiesEnabled else { return false }

    let id = String(cString: journeyId)

    // One journey, one activity. Ending and restarting across a single journey
    // is what left three cards started and cleared in seventy minutes.
    if let running = liveActivity as? Activity<BusActivityAttributes>,
       running.attributes.journeyId == id {
        return true
    }

    guard let state = decodeState(stateJson) else { return false }

    let attributes = BusActivityAttributes(
        journeyId: id,
        riderName: String(cString: riderName),
        childId: childId
    )

    let content = ActivityContent(state: state, staleDate: Date().addingTimeInterval(15 * 60))

    // Ask for a push token first, and fall back to a purely local activity.
    //
    // Requesting .token needs the push entitlement, which needs the capability
    // enabled on the App ID. Where that is missing this throws -- and treating
    // that as fatal would mean NO card at all, when a locally updated one still
    // works perfectly whenever the app is running. Degrading is strictly better
    // than refusing: the parent gets a card on the lock screen either way, and
    // it simply stops advancing once iOS suspends the app.
    var activity: Activity<BusActivityAttributes>?

    do {
        activity = try Activity.request(attributes: attributes, content: content, pushType: .token)
        pushAvailable = true
    } catch {
        do {
            activity = try Activity.request(attributes: attributes, content: content, pushType: nil)
            pushAvailable = false
        } catch {
            return false
        }
    }

    guard let started = activity else { return false }
    liveActivity = started

    if pushAvailable {
        Task {
            for await tokenData in started.pushTokenUpdates {
                let hex = tokenData.map { String(format: "%02x", $0) }.joined()
                hex.withCString { tokenCallback?($0) }
            }
        }
    }

    return true
}

/// Whether the running activity can be updated by push.
///
/// False means the card is local-only: correct while the app is running, frozen
/// once iOS suspends it. Worth surfacing rather than leaving a parent to wonder
/// why the countdown stopped.
@_cdecl("wwtb_activity_has_push")
public func wwtb_activity_has_push() -> Bool {
    pushAvailable
}

/// Update the running activity from the foreground.
///
/// While the app is open there is no reason to make a round trip through APNs
/// to change something this process already knows.
@_cdecl("wwtb_update_activity")
public func wwtb_update_activity(_ stateJson: UnsafePointer<CChar>) -> Bool {
    guard #available(iOS 16.2, *),
          let activity = liveActivity as? Activity<BusActivityAttributes>,
          let state = decodeState(stateJson)
    else { return false }

    Task {
        await activity.update(.init(state: state, staleDate: Date().addingTimeInterval(15 * 60)))
    }
    return true
}

/// End the activity, leaving the finished card up for a moment.
///
/// That the LAST thing shown is a FINISHED card matters more than it looks:
/// nothing clears it afterwards, so whatever it said last is what stands on the
/// phone until iOS retires it. Ending mid-ride left "riding home, 7 min" frozen
/// there.
@_cdecl("wwtb_end_activity")
public func wwtb_end_activity(_ stateJson: UnsafePointer<CChar>) -> Bool {
    guard #available(iOS 16.2, *),
          let activity = liveActivity as? Activity<BusActivityAttributes>
    else { return false }

    let state = decodeState(stateJson)
    Task {
        await activity.end(
            state.map { .init(state: $0, staleDate: nil) },
            dismissalPolicy: .after(Date().addingTimeInterval(3 * 60))
        )
    }
    liveActivity = nil
    return true
}

/// The journey id of the running activity, or an empty string.
@_cdecl("wwtb_current_journey")
public func wwtb_current_journey() -> UnsafePointer<CChar>? {
    guard #available(iOS 16.2, *),
          let activity = liveActivity as? Activity<BusActivityAttributes>
    else { return nil }

    // Caller copies immediately; strdup keeps it alive across the boundary.
    return UnsafePointer(strdup(activity.attributes.journeyId))
}

/// Decode the same content-state shape the worker pushes, so there is exactly
/// one contract rather than two that can drift apart.
@available(iOS 16.2, *)
private func decodeState(_ json: UnsafePointer<CChar>) -> BusActivityAttributes.ContentState? {
    let text = String(cString: json)
    guard let data = text.data(using: .utf8) else { return nil }
    return try? JSONDecoder().decode(BusActivityAttributes.ContentState.self, from: data)
}
