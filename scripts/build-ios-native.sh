#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Builds the Swift the app links against: the ActivityKit bridge plus the shared
# BusActivityAttributes type.
#
# WidgetKit UI cannot be written in C#, so the Live Activity's LAYOUT is SwiftUI
# in a widget extension, and driving ActivityKit from C# needs these @_cdecl
# entry points. This script produces the xcframework the .csproj picks up; the
# widget extension itself is built by Xcode (see docs/ios-build.md).

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BUILD="$ROOT/ios/build"
NAME="WhenWillTheBusNative"
MIN_IOS="17.0"

SOURCES=(
  "$ROOT/ios/LiveActivity/BusActivityAttributes.swift"
  "$ROOT/ios/App/LiveActivityBridge.swift"
)

rm -rf "$BUILD"
mkdir -p "$BUILD"

build_slice() {
  local slice="$1" sdk="$2" target="$3"
  local out="$BUILD/$slice"
  mkdir -p "$out"

  echo "  building $slice"
  xcrun --sdk "$sdk" swiftc \
    -target "$target" \
    -emit-library -static \
    -emit-module -module-name "$NAME" \
    -emit-module-path "$out/$NAME.swiftmodule" \
    -parse-as-library \
    -O \
    -o "$out/lib$NAME.a" \
    "${SOURCES[@]}"

  # A headerless static library: C# reaches these through DllImport("__Internal")
  # by symbol name, so no generated header is involved.
  mkdir -p "$out/Headers"
  cat > "$out/Headers/$NAME.h" <<'HEADER'
// SPDX-License-Identifier: GPL-3.0-or-later
// Entry points exported by the Swift bridge, resolved from C# through
// DllImport("__Internal").
#ifndef WHENWILLTHEBUS_NATIVE_H
#define WHENWILLTHEBUS_NATIVE_H

#include <stdbool.h>
#include <stdint.h>

bool wwtb_activities_enabled(void);
void wwtb_set_token_callback(void (*callback)(const char *));
bool wwtb_start_activity(const char *journeyId, const char *riderName, const char *busNumber, int64_t childId, const char *stateJson);
bool wwtb_update_activity(const char *stateJson);
bool wwtb_end_activity(const char *stateJson);
const char *wwtb_current_journey(void);

#endif
HEADER
}

echo "Building the ActivityKit bridge..."
build_slice "device" "iphoneos" "arm64-apple-ios$MIN_IOS"
build_slice "simulator" "iphonesimulator" "arm64-apple-ios$MIN_IOS-simulator"

echo "Packaging $NAME.xcframework"
xcodebuild -create-xcframework \
  -library "$BUILD/device/lib$NAME.a" -headers "$BUILD/device/Headers" \
  -library "$BUILD/simulator/lib$NAME.a" -headers "$BUILD/simulator/Headers" \
  -output "$BUILD/$NAME.xcframework" > /dev/null

echo "Done: ios/build/$NAME.xcframework"
echo
echo "Next: build the widget extension in Xcode -- see docs/ios-build.md."
echo "BusActivityAttributes.swift must belong to BOTH the app target and the"
echo "widget extension target, or the card starts and then ignores every update."
