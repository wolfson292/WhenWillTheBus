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

# --help, and rejecting arguments rather than ignoring them.
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/_help.sh"
wwtb_help "${BASH_SOURCE[0]}" "$@"

INSTALL=0
for argument in "$@"; do
  case "$argument" in
    --install) INSTALL=1 ;;
    -*) wwtb_unknown "${BASH_SOURCE[0]}" "$argument" ;;
  esac
done
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
# No instruction to change APNS_SANDBOX. This build tells the worker which
# environment its tokens belong to when it registers, and the worker picks the
# APNs host per activity -- so a development phone and a TestFlight household
# are served at once and neither setting breaks the other. Telling someone to
# flip a global switch for a device build would break the family's cards to fix
# nothing.
echo "==> APNs           $APNS_ENV  (declared per registration; the worker needs no change)"

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

if [ "$INSTALL" = "1" ]; then
  # Read the JSON, not the text table.
  #
  # devicectl's human-readable table shifts its columns with the longest device
  # name, so an awk field index is right until somebody renames a phone. The
  # JSON names what it means: a device is usable when its tunnel is connected,
  # whatever the table happens to look like. (provision.sh already did this.)
  #
  # Retried, because a phone that locks or a CoreDevice reconnect makes the
  # device vanish for a few seconds, and failing a whole build over a blink that
  # clears itself is worse than waiting for it.
  DEVICE=""
  for attempt in 1 2 3 4 5 6; do
    xcrun devicectl list devices --json-output /tmp/wwtb-devices.json >/dev/null 2>&1
    DEVICE=$(python3 -c "
import json
try:
    d = json.load(open('/tmp/wwtb-devices.json'))
except Exception:
    raise SystemExit
paired = []
for x in d.get('result', {}).get('devices', []):
    hw = x.get('hardwareProperties', {})
    conn = x.get('connectionProperties', {})
    # PAIRED, not 'tunnel currently connected'. The tunnel flaps -- it drops
    # whenever the phone locks and devicectl re-establishes it on demand, which
    # is why a launch succeeds a second after a listing called it disconnected.
    # Requiring the tunnel here failed builds against a perfectly usable phone.
    if hw.get('serialNumber') and conn.get('pairingState') == 'paired':
        paired.append(hw.get('udid'))
        if conn.get('tunnelState') == 'connected':
            print(hw.get('udid'))
            break
else:
    if paired:
        print(paired[0])
" 2>/dev/null)
    [ -n "$DEVICE" ] && break
    [ "$attempt" = 1 ] && echo "    waiting for the device..."
    sleep 5
  done

  [ -n "$DEVICE" ] || {
    echo "error: no paired physical device after 30s." >&2
    echo "       Unlock the phone, then: xcrun devicectl list devices" >&2
    exit 1
  }

  echo "==> Installing on $DEVICE"
  xcrun devicectl device install app --device "$DEVICE" "$APP"
fi

echo
echo "Built: $APP"
