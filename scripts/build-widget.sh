#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Builds the Live Activity widget extension and embeds it in the MAUI app.
#
# WidgetKit UI cannot be written in C#, so the card's layout is a SwiftUI app
# extension -- a separate process with its own bundle, which the .app carries in
# PlugIns/. Everything deciding WHAT the card says stays in C#.
#
# Usage:
#   scripts/build-widget.sh                 # simulator, Debug host
#   scripts/build-widget.sh device Release  # device, needs DEVELOPMENT_TEAM

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DESTINATION="${1:-simulator}"
HOST_CONFIG="${2:-Debug}"

# One source of truth, so the app, the extension and the worker's APNs topic
# cannot drift apart. See Directory.Build.props.
WWTB_BUNDLE_ID=$(sed -n 's/.*<ApplicationId[^>]*>\([^<]*\)<.*/\1/p' "$ROOT/Directory.Build.props" | head -1)
[ -n "$WWTB_BUNDLE_ID" ] || { echo "error: no ApplicationId in Directory.Build.props" >&2; exit 1; }
export WWTB_BUNDLE_ID

if [ "$DESTINATION" = "device" ]; then
  SDK="iphoneos"
  RID="ios-arm64"
  if [ -z "${DEVELOPMENT_TEAM:-}" ]; then
    echo "error: a device build must be signed. Set DEVELOPMENT_TEAM=XXXXXXXXXX." >&2
    exit 1
  fi
  SIGNING=(DEVELOPMENT_TEAM="$DEVELOPMENT_TEAM" CODE_SIGNING_ALLOWED=YES CODE_SIGNING_REQUIRED=YES)
else
  SDK="iphonesimulator"
  RID="iossimulator-arm64"
  SIGNING=()
fi

echo "==> Regenerating the widget project from ios/project.yml"
xcodegen generate --spec "$ROOT/ios/project.yml" --project "$ROOT/ios" >/dev/null

echo "==> Building BusWidget ($SDK, Release)"
xcodebuild \
  -project "$ROOT/ios/WhenWillTheBusWidget.xcodeproj" \
  -scheme BusWidget \
  -sdk "$SDK" \
  -configuration Release \
  -derivedDataPath "$ROOT/ios/build/dd" \
  "${SIGNING[@]}" \
  build > /dev/null

APPEX="$ROOT/ios/build/dd/Build/Products/Release-$SDK/BusWidget.appex"
[ -d "$APPEX" ] || { echo "error: BusWidget.appex was not produced" >&2; exit 1; }

APP="$ROOT/src/WhenWillTheBus.App/bin/$HOST_CONFIG/net10.0-ios/$RID/WhenWillTheBus.App.app"
if [ ! -d "$APP" ]; then
  echo "error: the app bundle is not built yet. Run first:" >&2
  echo "  dotnet build src/WhenWillTheBus.App -c $HOST_CONFIG" >&2
  exit 1
fi

# PlugIns, with that exact spelling. iOS looks nowhere else, and an extension in
# the wrong directory is not an error -- it is simply never loaded, which reads
# on the phone as a Live Activity that will not start.
echo "==> Embedding into $(basename "$APP")/PlugIns"
mkdir -p "$APP/PlugIns"
rm -rf "$APP/PlugIns/BusWidget.appex"
cp -R "$APPEX" "$APP/PlugIns/"

APP_ID=$(plutil -extract CFBundleIdentifier raw "$APP/Info.plist")
EXT_ID=$(plutil -extract CFBundleIdentifier raw "$APP/PlugIns/BusWidget.appex/Info.plist")

# An extension's bundle id must sit UNDER its host's. If it does not, iOS
# rejects the app at install time with a message that does not mention this.
case "$EXT_ID" in
  "$APP_ID".*) ;;
  *) echo "error: extension id '$EXT_ID' is not under app id '$APP_ID'" >&2; exit 1 ;;
esac

echo
echo "    app       $APP_ID"
echo "    extension $EXT_ID"
echo "    embedded  $APP/PlugIns/BusWidget.appex"
