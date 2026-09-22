// SPDX-License-Identifier: GPL-3.0-or-later

import ActivityKit
import Foundation

/// The identity and content of one journey's Live Activity.
///
/// EVERY PROPERTY OF `ContentState` MUST MATCH `BusActivityState` in
/// src/WhenWillTheBus.Server/LiveActivity/LiveActivityState.cs, name for name.
/// A mismatch is a push that Apple accepts and the widget silently ignores,
/// which is among the least debuggable failures in the whole system — there is
/// no error, no log line, and nothing on the phone.
///
/// Instants cross the wire as whole-second epoch numbers rather than ISO
/// strings, because Swift's `.iso8601` decoding strategy rejects fractional
/// seconds and .NET writes them by default. Numbers cannot be got wrong.
struct BusActivityAttributes: ActivityAttributes {
    struct ContentState: Codable, Hashable {
        /// One of: idle, to_stop, at_stop, to_school, at_school, from_school, home.
        var stage: String

        /// The instant being counted towards, already rounded to the displayed minute.
        var target: TimeInterval?

        /// The observed range this arrival has fallen in — not a statistical
        /// interval, and it narrows on its own as the bus closes in.
        var earliest: TimeInterval?
        var latest: TimeInterval?

        /// When the last GPS fix was taken. NOT its age: the OS renders
        /// "3 minutes ago" from an instant by itself, and only redraws when a
        /// new fix actually lands.
        var fixedAt: TimeInterval?

        /// 0-100 through whatever the current stage measures.
        var progress: Int?

        var distanceMiles: Double?

        /// route, approach, historical, scheduled or unknown.
        var basis: String

        var targetDate: Date? { target.map(Date.init(timeIntervalSince1970:)) }
        var earliestDate: Date? { earliest.map(Date.init(timeIntervalSince1970:)) }
        var latestDate: Date? { latest.map(Date.init(timeIntervalSince1970:)) }
        var fixedAtDate: Date? { fixedAt.map(Date.init(timeIntervalSince1970:)) }

        /// What the card calls this stage.
        var title: String {
            switch stage {
            case "to_stop": "Bus on the way"
            case "at_stop": "Bus is here"
            case "to_school": "Riding to school"
            case "at_school": "At school"
            case "from_school": "Riding home"
            case "home": "Home"
            default: "No bus today"
            }
        }

        /// Whether the target is worth counting down to, or is simply the end.
        var isArrived: Bool { stage == "at_stop" || stage == "at_school" || stage == "home" }

        /// How confident the estimate is entitled to sound.
        ///
        /// A window centred on a timetable that is twenty minutes out is the
        /// failure that hides behind a confident-looking card, so a scheduled
        /// estimate says so rather than looking like a measurement.
        var qualifier: String? {
            switch basis {
            case "route": nil
            case "approach": "estimated"
            case "historical": "usual time"
            case "scheduled": "timetable"
            default: nil
            }
        }
    }

    /// Stable for the life of one journey, e.g. "20260917-am".
    ///
    /// Used as the activity's identity so a new journey is a new activity and a
    /// failed start leaves nothing wedged. Derived from the CLOCK rather than
    /// from the prediction's run, which flips to the afternoon the instant the
    /// morning pickup passes and would rename a journey halfway through it.
    var journeyId: String

    var riderName: String

    /// The bus as the school numbers it, or empty when the roster has none.
    ///
    /// Static for the life of the journey, so it belongs here rather than in
    /// `ContentState` — which keeps it out of the pushed payload, and therefore
    /// out of the contract the worker has to match field for field.
    var busNumber: String

    var childId: Int64
}
