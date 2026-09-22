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
                    HStack(spacing: 6) {
                        BusMark(tint: context.state.accent, width: 16)
                        Text(context.attributes.riderName)
                            .font(.caption.weight(.semibold))
                    }
                }

                DynamicIslandExpandedRegion(.trailing) {
                    ArrivalTime(state: context.state)
                        .font(.title3.weight(.bold))
                        .foregroundStyle(context.state.accent)
                        .monospacedDigit()
                }

                DynamicIslandExpandedRegion(.bottom) {
                    VStack(alignment: .leading, spacing: 7) {
                        HStack {
                            Text(context.state.title)
                                .font(.caption)
                                .foregroundStyle(.secondary)
                            Spacer()
                            ConfidenceDots(filled: context.state.confidence, tint: context.state.accent)
                        }

                        if context.state.progress != nil {
                            RouteTrack(fraction: context.state.fraction, tint: context.state.accent)
                        }

                        HStack {
                            BandFootnote(state: context.state)
                            Spacer()
                            FixAge(state: context.state)
                        }
                    }
                }
            } compactLeading: {
                BusMark(tint: context.state.accent, width: 16)
            } compactTrailing: {
                // Lead with the TIME, not the distance. A watch and the compact
                // island both truncate hard, and the distance is already drawn
                // as the bar -- "4.5 miles from..." spent the whole visible line
                // restating it.
                ArrivalTime(state: context.state)
                    .font(.caption2.weight(.bold))
                    .foregroundStyle(context.state.accent)
                    .monospacedDigit()
            } minimal: {
                BusMark(tint: context.state.accent, width: 15)
            }
            // FOLLOWS THE STATE, not a fixed yellow. This ring is often the only
            // colour on screen, and yellow while the rider is already aboard
            // answers a question nobody is asking any more.
            .keylineTint(context.state.accent)
        }
    }
}

private struct LockScreenCard: View {
    let context: ActivityViewContext<BusActivityAttributes>

    private var state: BusActivityAttributes.ContentState { context.state }

    var body: some View {
        VStack(alignment: .leading, spacing: 11) {
            HStack(spacing: 7) {
                BusMark(tint: state.accent, width: 17)

                Text(context.attributes.riderName)
                    .font(.subheadline.weight(.semibold))

                if !context.attributes.busNumber.isEmpty {
                    Text("Bus \(context.attributes.busNumber)")
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                }

                Spacer(minLength: 8)

                // HOW MUCH THIS IS ENTITLED TO CLAIM, in the top right where a
                // status usually goes. A timetable guess and a live route match
                // are the same shape of number, and must never be the same
                // shape of thing on screen.
                ConfidenceDots(filled: state.confidence, tint: state.accent)

                Text(state.confidenceLabel)
                    .font(.caption2.weight(.semibold))
                    .foregroundStyle(state.confidence >= 3 ? AnyShapeStyle(state.accent) : AnyShapeStyle(.secondary))
            }

            HStack(alignment: .lastTextBaseline) {
                VStack(alignment: .leading, spacing: 3) {
                    Text(state.title)
                        .font(.headline)
                    Subtitle(state: state)
                }

                Spacer(minLength: 10)

                VStack(alignment: .trailing, spacing: 3) {
                    ArrivalTime(state: state)
                        .font(.title.weight(.bold))
                        .foregroundStyle(state.accent)
                        .monospacedDigit()
                    DueAt(state: state)
                }
            }

            if state.progress != nil {
                RouteTrack(fraction: state.fraction, tint: state.accent)
            }

            HStack {
                Text("your stop")
                    .font(.caption2)
                    .foregroundStyle(.tertiary)
                Spacer()
                FixAge(state: state)
            }
        }
        .padding()
    }
}

/// The time the bus is expected, or how long that is from now.
private struct ArrivalTime: View {
    let state: BusActivityAttributes.ContentState

    var body: some View {
        if state.isArrived {
            Text("now")
        } else if let target = state.targetDate {
            if state.countdownIsHonest {
                // Rendered by the OS from an instant, so it keeps counting down
                // between pushes -- which is exactly why the server must re-push
                // when the target MOVES. A backstop alone would leave this
                // counting confidently down to a time that is no longer true.
                Text(target, style: .timer)
            } else {
                // Nothing here is measuring anything minute by minute, so do not
                // draw something that looks like it is.
                Text(target, style: .time)
            }
        } else {
            Text("--")
        }
    }
}

/// The clock time, when the headline is a running countdown.
private struct DueAt: View {
    let state: BusActivityAttributes.ContentState

    var body: some View {
        if !state.isArrived, state.countdownIsHonest, let target = state.targetDate {
            Text("due \(target, style: .time)")
                .font(.caption2)
                .foregroundStyle(.secondary)
        }
    }
}

/// The one line under the stage: how far out, and the range it has fallen in.
private struct Subtitle: View {
    let state: BusActivityAttributes.ContentState

    var body: some View {
        if !state.isReporting {
            Text("not reporting")
                .font(.caption)
                .foregroundStyle(BusPalette.fault)
        } else if let miles = state.distanceMiles {
            Text(String(format: "%.1f miles out", miles))
                .font(.caption)
                .foregroundStyle(.secondary)
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
                .foregroundStyle(state.isReporting ? AnyShapeStyle(.tertiary) : AnyShapeStyle(BusPalette.fault))
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
