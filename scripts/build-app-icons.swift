#!/usr/bin/env swift
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Draws the three iOS 18 app-icon appearances into the asset catalog.
//
// WHY THIS EXISTS AT ALL. MauiIcon rasterises one SVG into one appearance, and
// there is no property that adds the others -- so an app built through it shows
// the same bright yellow tile on a dark home screen at night and ignores a tint
// the owner chose. Supplying all three means owning the appiconset by hand, and
// owning it by hand means something has to produce 1024px PNGs.
//
// CoreGraphics rather than an SVG rasteriser because this machine has none, and
// adding a build dependency to draw five rectangles would be worse than drawing
// them. The geometry below is the SAME 1024 grid as
// src/WhenWillTheBus.App/Resources/AppIcon/appicon.svg, which stays the
// readable source of truth -- change one, re-run this, and check them against
// each other.
//
//     swift scripts/build-app-icons.swift
//
// The PNGs it writes are committed, so an ordinary build needs neither Swift nor
// this script.

import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

let side: CGFloat = 1024

// The 1024 grid, shared with appicon.svg.
let bodyRect = CGRect(x: 112, y: 144, width: 800, height: 736)
let bodyRadius: CGFloat = 190
let screenRect = CGRect(x: 208, y: 256, width: 608, height: 272)
let screenRadius: CGFloat = 112
let lampRadius: CGFloat = 66
let lampY: CGFloat = 708
let lampLeftX: CGFloat = 284
let lampRightX: CGFloat = 740
let grilleRect = CGRect(x: 426, y: 680, width: 172, height: 56)
let grilleRadius: CGFloat = 28

struct Appearance {
    let name: String

    /// Nil leaves the ground transparent.
    let ground: UInt32?
    let body: UInt32
    let screen: UInt32
    let lamp: UInt32

    /// The grille is a tone of the screen colour, and disappears on its own at
    /// small sizes. It is the only element that does.
    let grilleAlpha: CGFloat
}

let appearances = [
    // Full-bleed and opaque, exactly as appicon.svg. iOS masks its own shape,
    // so there are no rounded corners here.
    Appearance(name: "appicon-any", ground: 0x0E1117, body: 0xF6C445, screen: 0x0E1117, lamp: 0xFFF4D6, grilleAlpha: 0.25),

    // Dimmer yellow on a deeper ground: a home screen at night should not be lit
    // by one app. Kept OPAQUE rather than transparent, so the mark reads the
    // same whatever backdrop the system puts behind it.
    Appearance(name: "appicon-dark", ground: 0x0B0D12, body: 0xD9AB33, screen: 0x0B0D12, lamp: 0xE8DCB8, grilleAlpha: 0.25),

    // GREYSCALE ON PURPOSE. iOS builds the tinted appearance from this image's
    // luminance, so any colour here is thrown away -- and the lamps must stay
    // lighter than the body or the whole thing flattens into one shape.
    Appearance(name: "appicon-tinted", ground: 0x16181C, body: 0xC9CDD4, screen: 0x16181C, lamp: 0xF2F4F7, grilleAlpha: 0.22),
]

func cgColor(_ hex: UInt32, alpha: CGFloat = 1) -> CGColor {
    CGColor(
        red: CGFloat((hex >> 16) & 0xFF) / 255,
        green: CGFloat((hex >> 8) & 0xFF) / 255,
        blue: CGFloat(hex & 0xFF) / 255,
        alpha: alpha
    )
}

func fill(_ context: CGContext, _ rect: CGRect, radius: CGFloat, _ colour: CGColor) {
    context.addPath(CGPath(roundedRect: rect, cornerWidth: radius, cornerHeight: radius, transform: nil))
    context.setFillColor(colour)
    context.fillPath()
}

func draw(_ appearance: Appearance) -> CGImage? {
    guard let context = CGContext(
        data: nil,
        width: Int(side),
        height: Int(side),
        bitsPerComponent: 8,
        bytesPerRow: 0,
        space: CGColorSpace(name: CGColorSpace.sRGB)!,
        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
    ) else { return nil }

    // CoreGraphics counts up from the bottom left; the grid above counts down
    // from the top left, like the SVG it mirrors.
    context.translateBy(x: 0, y: side)
    context.scaleBy(x: 1, y: -1)
    context.setAllowsAntialiasing(true)

    if let ground = appearance.ground {
        context.setFillColor(cgColor(ground))
        context.fill(CGRect(x: 0, y: 0, width: side, height: side))
    }

    fill(context, bodyRect, radius: bodyRadius, cgColor(appearance.body))
    fill(context, screenRect, radius: screenRadius, cgColor(appearance.screen))

    context.setFillColor(cgColor(appearance.lamp))
    for x in [lampLeftX, lampRightX] {
        context.fillEllipse(in: CGRect(
            x: x - lampRadius,
            y: lampY - lampRadius,
            width: lampRadius * 2,
            height: lampRadius * 2
        ))
    }

    fill(context, grilleRect, radius: grilleRadius, cgColor(appearance.screen, alpha: appearance.grilleAlpha))

    return context.makeImage()
}

let root = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
let outputDirectory = root
    .appendingPathComponent("src/WhenWillTheBus.App/Platforms/iOS/AppIcons.xcassets/AppIcon.appiconset")

try FileManager.default.createDirectory(at: outputDirectory, withIntermediateDirectories: true)

for appearance in appearances {
    guard let image = draw(appearance) else {
        FileHandle.standardError.write("error: could not draw \(appearance.name)\n".data(using: .utf8)!)
        exit(1)
    }

    let url = outputDirectory.appendingPathComponent("\(appearance.name).png")
    guard let destination = CGImageDestinationCreateWithURL(
        url as CFURL,
        UTType.png.identifier as CFString,
        1,
        nil
    ) else {
        FileHandle.standardError.write("error: could not open \(url.path)\n".data(using: .utf8)!)
        exit(1)
    }

    CGImageDestinationAddImage(destination, image, nil)
    guard CGImageDestinationFinalize(destination) else {
        FileHandle.standardError.write("error: could not write \(url.path)\n".data(using: .utf8)!)
        exit(1)
    }

    print("  wrote \(appearance.name).png")
}

print("Done: \(outputDirectory.path)")
