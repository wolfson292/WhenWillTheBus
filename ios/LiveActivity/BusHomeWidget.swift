// SPDX-License-Identifier: GPL-3.0-or-later

import SwiftUI
import WidgetKit

/// The next arrival, on the Home Screen and in the Lock Screen complications.
///
/// Reads the snapshot the app leaves in the shared App Group container. The
/// widget never predicts anything itself -- it wakes, draws and goes away, and
/// the prediction is a C# engine that needs history and a network.
struct BusHomeWidget: Widget {
    var body: some WidgetConfiguration {
        StaticConfiguration(kind: "BusHomeWidget", provider: SnapshotProvider()) { entry in
            BusWidgetView(snapshot: entry.snapshot)
                .containerBackground(.fill.tertiary, for: .widget)
        }
        .configurationDisplayName("Next bus")
        .description("When the school bus is due at your stop.")
        .supportedFamilies([
            .systemSmall,
            .systemMedium,
            .accessoryRectangular,
            .accessoryInline,
        ])
    }
}

struct SnapshotEntry: TimelineEntry {
    let date: Date
    let snapshot: BusSnapshot?
}

struct SnapshotProvider: TimelineProvider {
    func placeholder(in context: Context) -> SnapshotEntry {
        SnapshotEntry(date: Date(), snapshot: nil)
    }

    func getSnapshot(in context: Context, completion: @escaping (SnapshotEntry) -> Void) {
        completion(SnapshotEntry(date: Date(), snapshot: BusSnapshot.read()))
    }

    func getTimeline(in context: Context, completion: @escaping (Timeline<SnapshotEntry>) -> Void) {
        let entry = SnapshotEntry(date: Date(), snapshot: BusSnapshot.read())

        // Re-read in fifteen minutes. The app refreshes the file whenever it
        // polls and asks WidgetKit to reload, so this is only the floor for a
        // phone nobody has opened -- asking more often would spend the widget
        // budget redrawing a number that has not changed.
        let next = Date().addingTimeInterval(15 * 60)
        completion(Timeline(entries: [entry], policy: .after(next)))
    }
}

struct BusWidgetView: View {
    @Environment(\.widgetFamily) private var family
    let snapshot: BusSnapshot?

    var body: some View {
        switch family {
        case .accessoryInline:
            Label(inlineText, systemImage: "bus.fill")
        case .accessoryRectangular:
            VStack(alignment: .leading, spacing: 2) {
                Text(snapshot?.title ?? "Next bus").font(.caption).foregroundStyle(.secondary)
                arrival.font(.title3.weight(.semibold))
            }
        default:
            VStack(alignment: .leading, spacing: 6) {
                Label(snapshot?.riderName.split(separator: " ").first.map(String.init) ?? "Bus",
                      systemImage: "bus.fill")
                    .font(.caption)
                    .foregroundStyle(.secondary)

                arrival.font(.title.weight(.semibold)).minimumScaleFactor(0.6).lineLimit(1)

                Text(snapshot?.title ?? "Nothing scheduled")
                    .font(.caption2)
                    .foregroundStyle(.secondary)
                    .lineLimit(2)

                if let qualifier = snapshot?.qualifier {
                    Text(qualifier).font(.caption2).foregroundStyle(.tertiary)
                }

                Spacer(minLength: 0)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    /// The time, not the distance. On the smallest widget it is the only line
    /// there is room for, and the distance says less.
    private var arrival: Text {
        guard let snapshot, !snapshot.isStale else { return Text("--") }

        if snapshot.stage == "at_stop" || snapshot.stage == "home" || snapshot.stage == "at_school" {
            return Text("now")
        }

        guard let target = snapshot.targetDate else { return Text("--") }
        return Text(target, style: .time)
    }

    private var inlineText: String {
        guard let snapshot, !snapshot.isStale, let target = snapshot.targetDate else { return "No bus" }
        return target.formatted(date: .omitted, time: .shortened)
    }
}
