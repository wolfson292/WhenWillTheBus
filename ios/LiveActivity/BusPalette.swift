// SPDX-License-Identifier: GPL-3.0-or-later

import SwiftUI

/// The colours, and the rules for choosing between them.
///
/// WIDGET TARGET ONLY. This file is deliberately not in the list
/// scripts/build-ios-native.sh compiles into the xcframework: that build has no
/// SwiftUI and exists only to carry the ActivityKit bridge. Anything the app and
/// the widget BOTH need lives in BusActivityAttributes.swift, which is why that
/// file holds no presentation at all.
enum BusPalette {
    /// The bus, and only the bus. Never a background, never a heading.
    static let bus = Color(red: 0.965, green: 0.769, blue: 0.271)

    /// Far off, or resting on nothing better than a timetable.
    static let far = Color(red: 0.431, green: 0.588, blue: 0.788)

    /// Scanned on, at school, home. The resolved states.
    static let aboard = Color(red: 0.247, green: 0.725, blue: 0.541)

    /// The bus stopped reporting.
    static let fault = Color(red: 1.0, green: 0.478, blue: 0.420)

    /// Inside this, an arrival is imminent enough to warm the card.
    static let soon: TimeInterval = 10 * 60

    /// Beyond this, a ticking countdown stops being a measurement and starts
    /// being a claim.
    static let countdownCeiling: TimeInterval = 20 * 60

    /// A fix older than this is not something to draw a live colour from.
    static let fixGoesQuiet: TimeInterval = 6 * 60
}

extension BusActivityAttributes.ContentState {
    /// Whether the bus has been seen recently enough to colour anything.
    var isReporting: Bool {
        guard let fixedAtDate else { return false }
        return Date().timeIntervalSince(fixedAtDate) < BusPalette.fixGoesQuiet
    }

    /// The colour this moment in the journey is entitled to.
    ///
    /// THE CARD WARMS AS THE BUS CLOSES IN, and does nothing else: no flashing,
    /// no pulsing, no animation. A card that demands attention every time the
    /// estimate is recalculated teaches people to stop looking at it.
    var accent: Color {
        // The rider is accounted for. Nothing about this is urgent, whatever the
        // clock says -- and a green card is the answer to the question the
        // parent actually has.
        if stage == "to_school" || stage == "at_school" || stage == "from_school" || stage == "home" {
            return BusPalette.aboard
        }

        // The bus going quiet is a state, not a failure to draw. Grey keeps the
        // last estimate on screen while withdrawing the claim that anything is
        // still backing it.
        guard isReporting else { return .secondary }

        if stage == "at_stop" { return BusPalette.bus }

        if stage == "to_stop", let targetDate, targetDate.timeIntervalSinceNow <= BusPalette.soon {
            return BusPalette.bus
        }

        return BusPalette.far
    }

    /// How much the estimate is entitled to claim, as a count out of four.
    var confidence: Int {
        switch basis {
        case "route": 4
        case "approach": 3
        case "historical": 2
        case "scheduled": 1
        default: 0
        }
    }

    /// What that count is called.
    var confidenceLabel: String {
        switch basis {
        case "route": "on route"
        case "approach": "estimated"
        case "historical": "usual time"
        case "scheduled": "timetable only"
        default: ""
        }
    }

    /// Whether a live countdown is honest here, or whether the clock time is.
    ///
    /// A ticking `6:04` reads as a measurement, and off a timetable it is not
    /// one: the published afternoon time here is nearly half an hour out, so a
    /// card counting confidently down from it is precisely the lie the
    /// prediction engine exists to avoid. Above twenty minutes the timer also
    /// stops looking like a countdown and starts looking like a clock.
    var countdownIsHonest: Bool {
        guard isReporting, basis == "route" || basis == "approach", let targetDate else { return false }
        let away = targetDate.timeIntervalSinceNow
        return away > 0 && away <= BusPalette.countdownCeiling
    }

    /// 0 to 1 through whatever the current stage measures, clamped for drawing.
    var fraction: Double {
        guard let progress else { return 0 }
        return min(max(Double(progress) / 100, 0), 1)
    }
}

/// The app mark, drawn at the proportions of the shipped icon.
///
/// The numbers are the 1024-grid ones from Resources/AppIcon/appicon.svg
/// divided through by the body box, so the glyph on a card and the icon on the
/// home screen are the same bus rather than two drawings of one.
struct BusMark: View {
    var tint: Color = BusPalette.bus
    var width: CGFloat = 17

    private var height: CGFloat { width * 0.92 }
    private var lamp: CGFloat { width * 0.165 }

    var body: some View {
        ZStack(alignment: .topLeading) {
            RoundedRectangle(cornerRadius: width * 0.2375, style: .continuous)
                .fill(tint)
                .frame(width: width, height: height)

            RoundedRectangle(cornerRadius: width * 0.14, style: .continuous)
                .fill(Color.black.opacity(0.88))
                .frame(width: width * 0.76, height: height * 0.37)
                .offset(x: width * 0.12, y: height * 0.152)

            Circle()
                .fill(Color.white.opacity(0.92))
                .frame(width: lamp, height: lamp)
                .offset(x: width * 0.215 - lamp / 2, y: height * 0.766 - lamp / 2)

            Circle()
                .fill(Color.white.opacity(0.92))
                .frame(width: lamp, height: lamp)
                .offset(x: width * 0.785 - lamp / 2, y: height * 0.766 - lamp / 2)
        }
        .frame(width: width, height: height)
        .accessibilityHidden(true)
    }
}

/// The visible form of `PredictionBasis`: four dots, filled as far as the
/// estimate has earned.
struct ConfidenceDots: View {
    let filled: Int
    let tint: Color
    var size: CGFloat = 5

    var body: some View {
        HStack(spacing: size * 0.7) {
            ForEach(0..<4, id: \.self) { index in
                Circle()
                    .fill(index < filled ? tint : Color.white.opacity(0.22))
                    .frame(width: size, height: size)
            }
        }
        .accessibilityHidden(true)
    }
}

/// How far along the route the bus is, with the bus riding the head of it.
struct RouteTrack: View {
    let fraction: Double
    let tint: Color
    var showsMark = true

    var body: some View {
        GeometryReader { geo in
            let head = geo.size.width * fraction

            ZStack(alignment: .leading) {
                Capsule()
                    .fill(Color.white.opacity(0.14))
                    .frame(height: 6)

                Capsule()
                    .fill(tint)
                    .frame(width: max(6, head), height: 6)

                if showsMark {
                    BusMark(tint: tint, width: 14)
                        .offset(x: min(max(0, head - 7), geo.size.width - 14))
                }
            }
            .frame(height: geo.size.height, alignment: .center)
        }
        .frame(height: showsMark ? 14 : 6)
        .accessibilityHidden(true)
    }
}
