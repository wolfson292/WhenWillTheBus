#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Everything needed to get the app, with a working Live Activity, onto a real
# iPhone. A Live Activity cannot meaningfully run in the simulator, so this is
# the only way to actually see the card.
#
# Usage:
#   DEVELOPMENT_TEAM=XXXXXXXXXX scripts/build-device.sh [--install]
#
# Find your team id with:  scripts/team-id.sh

set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

if [ -z "${DEVELOPMENT_TEAM:-}" ]; then
  echo "error: DEVELOPMENT_TEAM is not set. Run scripts/team-id.sh to find it." >&2
  exit 1
fi

BUNDLE_ID=$(sed -n 's/.*<ApplicationId[^>]*>\([^<]*\)<.*/\1/p' Directory.Build.props | head -1)

if [ "$BUNDLE_ID" = "com.example.whenwillthebus" ]; then
  echo "error: the bundle id is still the placeholder." >&2
  echo "       Apple will not sign com.example.*. Edit ApplicationId in" >&2
  echo "       Directory.Build.props to something your team owns." >&2
  exit 1
fi

APNS_ENV="${APNS_ENVIRONMENT:-development}"

echo "==> Bundle id      $BUNDLE_ID"
echo "==> Team           $DEVELOPMENT_TEAM"
echo "==> APNs           $APNS_ENV  (worker needs APNS_SANDBOX=$([ "$APNS_ENV" = development ] && echo true || echo false))"

echo "==> 1/4 ActivityKit bridge"
./scripts/build-ios-native.sh > /dev/null

echo "==> 2/4 App (device, Release, signed)"
dotnet build src/WhenWillTheBus.App \
  -c Release \
  -p:RuntimeIdentifier=ios-arm64 \
  -p:CodesignKey="Apple Development" \
  -p:CodesignProvision="Automatic" \
  -p:CodesignTeam="$DEVELOPMENT_TEAM" \
  -p:ApnsEnvironment="$APNS_ENV" \
  -v q --nologo

echo "==> 3/4 Widget extension, embedded"
./scripts/build-widget.sh device Release

APP="src/WhenWillTheBus.App/bin/Release/net10.0-ios/ios-arm64/WhenWillTheBus.App.app"

echo "==> 4/4 Checks"
EXT_ID=$(plutil -extract CFBundleIdentifier raw "$APP/PlugIns/BusWidget.appex/Info.plist")
LIVE=$(plutil -extract NSSupportsLiveActivities raw "$APP/Info.plist" 2>/dev/null || echo "MISSING")
APS=$(codesign -d --entitlements - --xml "$APP" 2>/dev/null | plutil -extract aps-environment raw - 2>/dev/null || echo "NONE")
echo "    extension      $EXT_ID"
echo "    aps-environment $APS"
echo "    live activities $LIVE"
[ "$LIVE" = "true" ] || { echo "error: NSSupportsLiveActivities is not set; the card will never start" >&2; exit 1; }

if [ "${1:-}" = "--install" ]; then
  DEVICE=$(xcrun devicectl list devices 2>/dev/null | awk '$0 !~ /simulated/ && /connected/ {print $3; exit}')
  [ -n "$DEVICE" ] || { echo "error: no physical device connected" >&2; exit 1; }
  echo "==> Installing on $DEVICE"
  xcrun devicectl device install app --device "$DEVICE" "$APP"
fi

echo
echo "Built: $APP"
