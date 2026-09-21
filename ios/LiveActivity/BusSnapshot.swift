// SPDX-License-Identifier: GPL-3.0-or-later

import Foundation

/// What the home-screen widget shows.
///
/// A widget cannot run the prediction: it wakes for a moment, draws, and goes
/// away. So the app writes this snapshot into the shared App Group container
/// after each poll and the widget just reads it.
///
/// EVERY FIELD HERE MUST MATCH `BusSnapshot` in
/// src/WhenWillTheBus.App/Services/WidgetSnapshot.cs. The two are written and
/// read by different processes in different languages, and a mismatch is a
/// widget that shows a placeholder for ever with nothing explaining why.
struct BusSnapshot: Codable {
    /// The App Group both sides reach through. Must match the entitlements.
    static let appGroup = "group.com.denlair.whenwillthebus"
    static let filename = "widget-snapshot.json"

    var riderName: String
    var stage: String
    var basis: String

    /// Whole-second epoch, for the same reason the Live Activity uses them:
    /// Swift's .iso8601 decoding rejects the fractional seconds .NET writes.
    var target: TimeInterval?
    var updatedAt: TimeInterval

    var distanceMiles: Double?
    var busNumber: String?

    var targetDate: Date? { target.map(Date.init(timeIntervalSince1970:)) }
    var updatedAtDate: Date { Date(timeIntervalSince1970: updatedAt) }

    /// Where the shared file lives, or nil if the App Group is not configured.
    static var url: URL? {
        FileManager.default
            .containerURL(forSecurityApplicationGroupIdentifier: appGroup)?
            .appendingPathComponent(filename)
    }

    static func read() -> BusSnapshot? {
        guard let url, let data = try? Data(contentsOf: url) else { return nil }
        return try? JSONDecoder().decode(BusSnapshot.self, from: data)
    }

    var title: String {
        switch stage {
        case "to_stop": "Bus on the way"
        case "at_stop": "Bus is here"
        case "to_school": "Riding to school"
        case "at_school": "At school"
        case "from_school": "Riding home"
        case "home": "Home"
        default: "Next bus"
        }
    }

    /// How much the estimate is entitled to claim.
    ///
    /// A timetable-only figure says so rather than looking like a measurement:
    /// the published afternoon time here is nearly half an hour out.
    var qualifier: String? {
        switch basis {
        case "route": nil
        case "approach": "estimated"
        case "historical": "usual time"
        case "scheduled": "timetable"
        default: nil
        }
    }

    /// A snapshot nothing has refreshed for hours is not worth showing as fact.
    var isStale: Bool { Date().timeIntervalSince(updatedAtDate) > 3 * 60 * 60 }
}
