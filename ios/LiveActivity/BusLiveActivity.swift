// SPDX-License-Identifier: GPL-3.0-or-later

import ActivityKit
import SwiftUI
import WidgetKit

/// The Live Activity card.
///
/// This is the one part of the app that cannot be written in C#: WidgetKit
/// requires SwiftUI, and a widget extension is a separate process with its own
/// bundle. Everything it displays is computed elsewhere — by the same C#
/// prediction engine that runs on the phone and on the server — so this file
/// only decides how to draw an answer, never what the answer is.
struct BusLiveActivity: Widget {
    var body: some WidgetConfiguration {
        ActivityConfiguration(for: BusActivityAttributes.self) { context in
            LockScreenCard(context: context)
                .activityBackgroundTint(Color.black.opacity(0.35))
                .activitySystemActionForegroundColor(.white)
        } dynamicIsland: { context in
            DynamicIsland {
                DynamicIslandExpandedRegion(.leading) {
                    Label(context.attributes.riderName, systemImage: "bus.fill")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }

                DynamicIslandExpandedRegion(.trailing) {
                    ArrivalTime(state: context.state)
                        .font(.title3.weight(.semibold))
                }

                DynamicIslandExpandedRegion(.bottom) {
                    VStack(alignment: .leading, spacing: 6) {
                        Text(context.state.title).font(.caption).foregroundStyle(.secondary)
                        ProgressTrack(state: context.state)
                        BandFootnote(state: context.state)
                    }
                }
            } compactLeading: {
                Image(systemName: "bus.fill")
            } compactTrailing: {
                // Lead with the TIME, not the distance. A watch and the compact
                // island both truncate hard, and the distance is already drawn
                // as the bar -- "4.5 miles from..." spent the whole visible line
                // restating it.
                ArrivalTime(state: context.state).font(.caption2.weight(.semibold))
            } minimal: {
                Image(systemName: "bus.fill")
            }
            .keylineTint(.yellow)
        }
    }
}

private struct LockScreenCard: View {
    let context: ActivityViewContext<BusActivityAttributes>

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .firstTextBaseline) {
                Label(context.attributes.riderName, systemImage: "bus.fill")
                    .font(.subheadline.weight(.medium))

                Spacer()

                if let qualifier = context.state.qualifier {
                    Text(qualifier)
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                }
            }

            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text(context.state.title)
                    .font(.headline)

                Spacer()

                ArrivalTime(state: context.state)
                    .font(.title2.weight(.semibold))
                    .monospacedDigit()
            }

            ProgressTrack(state: context.state)

            HStack {
                BandFootnote(state: context.state)
                Spacer()
                FixAge(state: context.state)
            }
        }
        .padding()
    }
}

/// The time the bus is expected, and how long that is from now.
private struct ArrivalTime: View {
    let state: BusActivityAttributes.ContentState

    var body: some View {
        if state.isArrived {
            Text("now")
        } else if let target = state.targetDate {
            // Rendered by the OS from an instant, so it keeps counting down
            // between pushes -- which is exactly why the server must re-push
            // when the target MOVES. A backstop alone would leave this counting
            // confidently down to a time that is no longer true.
            Text(target, style: .timer)
        } else {
            Text("--")
        }
    }
}

/// How far through the current stage the journey is.
private struct ProgressTrack: View {
    let state: BusActivityAttributes.ContentState

    var body: some View {
        if let progress = state.progress {
            ProgressView(value: Double(progress), total: 100)
                .progressViewStyle(.linear)
                .tint(.yellow)
        }
    }
}

/// The observed range this arrival has fallen in.
///
/// Not a statistical interval -- off a handful of journeys the honest thing to
/// show is the range actually seen. It closes towards nothing as the bus nears
/// the stop, because what is left to vary is the part still to run.
private struct BandFootnote: View {
    let state: BusActivityAttributes.ContentState

    var body: some View {
        if !state.isArrived, let earliest = state.earliestDate, let latest = state.latestDate {
            Text("\(earliest, style: .time) – \(latest, style: .time)")
                .font(.caption2)
                .foregroundStyle(.secondary)
        }
    }
}

/// When the bus was last actually seen.
///
/// Reports the INSTANT, not an age in minutes. An age changes every single
/// minute a bus is running; the OS renders "3 minutes ago" from an instant by
/// itself and redraws only when a new fix lands.
private struct FixAge: View {
    let state: BusActivityAttributes.ContentState

    var body: some View {
        if let fixedAt = state.fixedAtDate {
            Text("seen \(fixedAt, style: .relative) ago")
                .font(.caption2)
                .foregroundStyle(.tertiary)
        }
    }
}

@main
struct BusWidgetBundle: WidgetBundle {
    var body: some Widget {
        BusLiveActivity()
        BusHomeWidget()
    }
}
