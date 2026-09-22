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

            // Minutes left as an arc. The one surface where the whole answer
            // fits in a shape rather than a sentence, which is what makes it
            // readable on a wrist without reading at all.
            .accessoryCircular,
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

extension BusSnapshot {
    /// The colour this snapshot is entitled to, by the same rule as the card.
    var accent: Color {
        if isStale { return .secondary }

        switch stage {
        case "to_school", "at_school", "from_school", "home": return BusPalette.aboard
        case "at_stop": return BusPalette.bus
        case "to_stop":
            guard let targetDate, targetDate.timeIntervalSinceNow <= BusPalette.soon else { return BusPalette.far }
            return BusPalette.bus
        default: return BusPalette.far
        }
    }

    /// Whole minutes until the bus is due, for the surfaces that only fit a
    /// number. Nil once there is nothing left to count.
    var minutesAway: Int? {
        guard !isStale, !isHere, let targetDate else { return nil }
        let away = targetDate.timeIntervalSinceNow
        guard away > 0 else { return nil }
        return max(1, Int(away / 60))
    }

    var isHere: Bool { stage == "at_stop" || stage == "home" || stage == "at_school" }

    /// How far round the ring to draw, over a one-hour horizon.
    ///
    /// An arc needs a scale, and "the whole journey" is not one the widget can
    /// know: the snapshot carries no start. An hour is the honest choice --
    /// a full ring simply means "not soon".
    var ringFraction: Double {
        guard let minutes = minutesAway else { return isHere ? 1 : 0 }
        return min(max(Double(minutes) / 60, 0.02), 1)
    }
}

struct BusWidgetView: View {
    @Environment(\.widgetFamily) private var family
    let snapshot: BusSnapshot?

    var body: some View {
        switch family {
        case .accessoryInline:
            Label(inlineText, systemImage: "bus.fill")

        case .accessoryCircular:
            CircularCountdown(snapshot: snapshot)

        case .accessoryRectangular:
            // LOCK SCREEN AND WATCH ARE DRAWN MONOCHROME. Any colour set here is
            // discarded by the system, so everything these sizes need to say has
            // to survive as weight, size and position alone.
            VStack(alignment: .leading, spacing: 2) {
                Text(snapshot?.title ?? "Next bus")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                HStack(alignment: .firstTextBaseline, spacing: 5) {
                    arrival.font(.title3.weight(.bold)).monospacedDigit()
                    if let minutes = snapshot?.minutesAway {
                        Text("in \(minutes)m")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }
                if let snapshot, snapshot.fraction > 0, !snapshot.isStale {
                    RouteTrack(fraction: snapshot.fraction, tint: .primary, showsMark: false)
                }
            }

        case .systemMedium:
            VStack(alignment: .leading, spacing: 8) {
                headline
                Facts(snapshot: snapshot)
            }

        default:
            headline
        }
    }

    /// The time, the stage and how far along. Shared by the small and medium
    /// sizes, because the medium one is the small one plus the day beside it.
    private var headline: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 5) {
                BusMark(tint: snapshot?.accent ?? .secondary, width: 15)
                Text(snapshot?.riderName.split(separator: " ").first.map(String.init) ?? "Bus")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                if let number = snapshot?.busNumber, !number.isEmpty {
                    Text("· \(number)")
                        .font(.caption)
                        .foregroundStyle(.tertiary)
                }
            }

            arrival
                .font(.system(.largeTitle, design: .rounded).weight(.bold))
                .monospacedDigit()
                .minimumScaleFactor(0.6)
                .lineLimit(1)

            if let minutes = snapshot?.minutesAway {
                Text("in \(minutes) min")
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(snapshot?.accent ?? .secondary)
            } else if let qualifier = snapshot?.qualifier {
                Text(qualifier)
                    .font(.caption2)
                    .foregroundStyle(.tertiary)
            }

            Spacer(minLength: 0)

            if let snapshot, snapshot.fraction > 0, !snapshot.isStale {
                RouteTrack(fraction: snapshot.fraction, tint: snapshot.accent, showsMark: false)
            }

            Text(subtitle)
                .font(.caption2)
                .foregroundStyle(.secondary)
                .lineLimit(2)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    /// The time, not the distance. On the smallest widget it is the only line
    /// there is room for, and the distance says less.
    private var arrival: Text {
        guard let snapshot, !snapshot.isStale else { return Text("--") }
        if snapshot.isHere { return Text("now") }
        guard let target = snapshot.targetDate else { return Text("--") }
        return Text(target, style: .time)
    }

    /// A snapshot nothing has refreshed for hours is not a fact. Say when it was
    /// written rather than letting this morning's estimate stand at four in the
    /// afternoon.
    private var subtitle: String {
        guard let snapshot else { return "Nothing scheduled" }
        if snapshot.isStale {
            return "last checked \(snapshot.updatedAtDate.formatted(date: .omitted, time: .shortened))"
        }
        return snapshot.title
    }

    private var inlineText: String {
        guard let snapshot, !snapshot.isStale, let target = snapshot.targetDate else { return "No bus" }
        return target.formatted(date: .omitted, time: .shortened)
    }
}

/// Minutes left as an arc, for the Lock Screen and the watch face.
private struct CircularCountdown: View {
    let snapshot: BusSnapshot?

    var body: some View {
        ZStack {
            Circle()
                .stroke(Color.primary.opacity(0.2), lineWidth: 5)

            Circle()
                .trim(from: 0, to: snapshot?.ringFraction ?? 0)
                .stroke(Color.primary, style: StrokeStyle(lineWidth: 5, lineCap: .round))
                .rotationEffect(.degrees(-90))

            if let minutes = snapshot?.minutesAway {
                VStack(spacing: -1) {
                    Text("\(minutes)")
                        .font(.system(.title3, design: .rounded).weight(.bold))
                    Text("min")
                        .font(.system(size: 9))
                        .foregroundStyle(.secondary)
                }
            } else if snapshot?.isHere == true {
                Image(systemName: "checkmark")
                    .font(.headline)
            } else {
                Image(systemName: "bus.fill")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .padding(2)
    }
}

/// The room the medium size has that the small one does not.
///
/// Only what the snapshot actually carries. The school-day timeline the design
/// sketches for this space would need arrival times the app does not write here,
/// and inventing a field to fill a layout is how a widget ends up confidently
/// showing a number nothing computed.
private struct Facts: View {
    let snapshot: BusSnapshot?

    var body: some View {
        HStack(spacing: 12) {
            if let miles = snapshot?.distanceMiles, snapshot?.isStale == false {
                Fact(label: "distance", value: String(format: "%.1f mi", miles))
            }

            if let qualifier = snapshot?.qualifier {
                Fact(label: "basis", value: qualifier)
            } else if snapshot?.basis == "route" {
                Fact(label: "basis", value: "on route")
            }

            Spacer(minLength: 0)
        }
    }

    private struct Fact: View {
        let label: String
        let value: String

        var body: some View {
            VStack(alignment: .leading, spacing: 2) {
                Text(label)
                    .font(.system(size: 10))
                    .foregroundStyle(.tertiary)
                Text(value)
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(.secondary)
            }
        }
    }
}
