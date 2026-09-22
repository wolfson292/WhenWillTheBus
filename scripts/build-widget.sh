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
#
# Set WWTB_DISTRIBUTION=1 for a TestFlight build. An embedded extension must be
# signed the SAME way as its host, and automatic signing picks DEVELOPMENT for a
# plain build however the host is signed -- which produces an .ipa Apple rejects
# at upload with ITMS-90035, after the upload.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DESTINATION="${1:-simulator}"
HOST_CONFIG="${2:-Debug}"

# One source of truth, so the app, the extension and the worker's APNs topic
# cannot drift apart. See Directory.Build.props.
WWTB_BUNDLE_ID=$(sed -n 's/.*<ApplicationId[^>]*>\([^<]*\)<.*/\1/p' "$ROOT/Directory.Build.props" | head -1)
[ -n "$WWTB_BUNDLE_ID" ] || { echo "error: no ApplicationId in Directory.Build.props" >&2; exit 1; }
export WWTB_BUNDLE_ID

# The extension carries its own version, and it must match the host app's or
# App Store Connect warns about the mismatch on every upload. Defaults suit a
# local build; scripts/build-testflight.sh passes the real build number.
export WWTB_VERSION="${WWTB_VERSION:-1.0}"
export WWTB_BUILD="${WWTB_BUILD:-1}"

if [ "$DESTINATION" = "device" ]; then
  SDK="iphoneos"
  RID="ios-arm64"
  if [ -z "${DEVELOPMENT_TEAM:-}" ]; then
    echo "error: a device build must be signed. Set DEVELOPMENT_TEAM=XXXXXXXXXX." >&2
    exit 1
  fi
  SIGNING=(DEVELOPMENT_TEAM="$DEVELOPMENT_TEAM" CODE_SIGNING_ALLOWED=YES CODE_SIGNING_REQUIRED=YES)
  export WWTB_TEAM="$DEVELOPMENT_TEAM"

  if [ "${WWTB_DISTRIBUTION:-0}" = "1" ]; then
    # Located now so the build does not run before failing. The profile Xcode
    # mints is found by its ENTITLEMENTS, not its name -- an App Store profile
    # is the one with get-task-allow FALSE -- because a build that breaks when
    # Apple renames a profile breaks for no reason anybody can see.
    WANT="$DEVELOPMENT_TEAM.$WWTB_BUNDLE_ID.BusWidget"
    DIST_PROFILE=""
    for prof in "$HOME/Library/Developer/Xcode/UserData/Provisioning Profiles"/*.mobileprovision; do
      [ -e "$prof" ] || continue
      PLIST=$(security cms -D -i "$prof" 2>/dev/null) || continue
      APPID=$(echo "$PLIST" | plutil -extract Entitlements.application-identifier raw - 2>/dev/null) || continue
      TASK=$(echo "$PLIST" | plutil -extract Entitlements.get-task-allow raw - 2>/dev/null || echo true)
      if [ "$APPID" = "$WANT" ] && [ "$TASK" = "false" ]; then
        DIST_PROFILE="$prof"
        break
      fi
    done

    [ -n "$DIST_PROFILE" ] || {
      echo "error: no App Store profile for $WANT." >&2
      echo "       Run: DEVELOPMENT_TEAM=$DEVELOPMENT_TEAM scripts/provision.sh --distribution" >&2
      exit 1
    }

    DIST_IDENTITY=$(security find-identity -v -p codesigning \
      | sed -n 's/.*"\(Apple Distribution: .*\)"/\1/p' | head -1)
    [ -n "$DIST_IDENTITY" ] || {
      echo "error: no Apple Distribution certificate in the keychain." >&2
      exit 1
    }
  fi

else
  SDK="iphonesimulator"
  RID="iossimulator-arm64"

  # The simulator needs no profile and must build without one.
  SIGNING=(CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO CODE_SIGN_IDENTITY="")
  export WWTB_TEAM=""
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

# RE-SIGNED, not built signed. Xcode will not use an Xcode-managed profile in
# manual mode, and a profile passed on the command line lands on every target in
# the project rather than this one -- so the distribution signature is applied
# here instead, exactly as Xcode's own export step applies it.
#
# The entitlements come FROM the profile, which is what makes them consistent
# with it: an entitlement the profile does not grant is stripped in silence.
if [ "${WWTB_DISTRIBUTION:-0}" = "1" ]; then
  echo "==> Re-signing the extension for distribution"
  EMBEDDED="$APP/PlugIns/BusWidget.appex"
  cp "$DIST_PROFILE" "$EMBEDDED/embedded.mobileprovision"

  ENT="$ROOT/ios/build/BusWidget.dist.entitlements"
  security cms -D -i "$DIST_PROFILE" \
    | plutil -extract Entitlements xml1 -o "$ENT" -

  codesign --force --timestamp=none \
    --sign "$DIST_IDENTITY" \
    --entitlements "$ENT" \
    "$EMBEDDED"

  codesign --verify --strict "$EMBEDDED"
fi

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
