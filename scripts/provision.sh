#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Mints the development provisioning profiles the device build needs.
#
# The real app is .NET MAUI, which is not an Xcode project, so there is nothing
# for `xcodebuild -allowProvisioningUpdates` to act on. ios/project.yml carries a
# ProvisioningHost stub with the SAME bundle id, the SAME entitlements, and the
# SAME embedded widget extension; provisioning it provisions exactly what the
# real build needs, and the stub itself is never installed.
#
# This REGISTERS THE DEVICE and CREATES APP IDs in your Apple Developer account.
#
# Usage:  DEVELOPMENT_TEAM=XXXXXXXXXX scripts/provision.sh

set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

[ -n "${DEVELOPMENT_TEAM:-}" ] || { echo "error: DEVELOPMENT_TEAM is not set. Run scripts/team-id.sh." >&2; exit 1; }

WWTB_BUNDLE_ID=$(sed -n 's/.*<ApplicationId[^>]*>\([^<]*\)<.*/\1/p' Directory.Build.props | head -1)
export WWTB_BUNDLE_ID
export WWTB_TEAM="$DEVELOPMENT_TEAM"

echo "==> Provisioning $WWTB_BUNDLE_ID and $WWTB_BUNDLE_ID.BusWidget"
xcodegen generate --spec ios/project.yml --project ios >/dev/null

# Target the connected device, not "generic/platform=iOS".
#
# Xcode registers a device with the account as a side effect of building FOR it.
# A generic destination gives it nothing to register, so the profile comes back
# covering only devices already on file -- and the install then fails with
# "This provisioning profile cannot be installed on this device", which does not
# mention registration at all.
DEVICE_UDID=$(xcrun devicectl list devices --json-output /tmp/wwtb-devices.json >/dev/null 2>&1 && python3 -c "
import json
try:
    d = json.load(open('/tmp/wwtb-devices.json'))
except Exception:
    raise SystemExit
for x in d.get('result', {}).get('devices', []):
    hw = x.get('hardwareProperties', {})
    state = x.get('connectionProperties', {}).get('tunnelState', '')
    if hw.get('serialNumber') and state != 'unavailable':
        print(hw.get('udid'))
        break
" 2>/dev/null || true)

if [ -n "$DEVICE_UDID" ]; then
  echo "    registering device $DEVICE_UDID"
  DESTINATION=(-destination "platform=iOS,id=$DEVICE_UDID")
else
  echo "    no device connected -- profiles will cover only devices already registered"
  DESTINATION=(-destination "generic/platform=iOS")
fi

# The widget target defaults to unsigned, which is right for the simulator and
# wrong here: an extension with no profile of its own cannot be embedded in a
# signed app. Overridden on the command line so the default stays simulator-safe.
xcodebuild \
  -project ios/WhenWillTheBusWidget.xcodeproj \
  -scheme ProvisioningHost \
  -sdk iphoneos \
  -configuration Release \
  "${DESTINATION[@]}" \
  -derivedDataPath ios/build/prov \
  DEVELOPMENT_TEAM="$DEVELOPMENT_TEAM" \
  CODE_SIGN_STYLE=Automatic \
  CODE_SIGNING_ALLOWED=YES \
  CODE_SIGNING_REQUIRED=YES \
  -allowProvisioningUpdates \
  build > "$ROOT/ios/build/provision.log" 2>&1 || {
    # The commonest failure by far, and the message does not say what to do.
    # -allowProvisioningUpdates creates App IDs and mints profiles, but it will
    # NOT register a device it has never seen: that is a portal action.
    if grep -q "isn't registered in your developer account" "$ROOT/ios/build/provision.log"; then
      cat >&2 <<REGISTER

This device is not registered in your Apple Developer account, and
-allowProvisioningUpdates will not add it for you.

  Device UDID:  ${DEVICE_UDID:-<connect the device first>}

  https://developer.apple.com/account/resources/devices/list
  -> + -> Register a Device -> paste the UDID above -> Continue -> Register

Then re-run this script.

REGISTER
      exit 3
    fi

    # Same shape as the device case: -allowProvisioningUpdates mints profiles
    # but will not turn on certain capabilities, and the error names the
    # entitlement rather than what to do about it.
    if grep -q "doesn't match the entitlements file" "$ROOT/ios/build/provision.log"; then
      MISSING=$(grep -o "value for the [a-zA-Z0-9.\-]* entitlement" "$ROOT/ios/build/provision.log" \
        | head -1 | sed 's/value for the //; s/ entitlement//')
      cat >&2 <<CAPABILITY

The profile does not grant an entitlement the build asks for:

  ${MISSING:-<see the log>}

Xcode creates App IDs and mints profiles, but it will not enable every
capability for you. Turn this one on for the App ID, then re-run:

  https://developer.apple.com/account/resources/identifiers/list
  -> $WWTB_BUNDLE_ID -> Capabilities

For App Groups, also tick the group itself under Edit, and check it exists
under Identifiers -> App Groups.

CAPABILITY
      exit 4
    fi

    echo "error: provisioning failed. Last lines:" >&2
    grep -iE "error|does not (support|have)|capability" "$ROOT/ios/build/provision.log" | sort -u | tail -10 >&2
    exit 1
  }

echo
DIR="$HOME/Library/Developer/Xcode/UserData/Provisioning Profiles"
PUSH_OK=1
for p in "$DIR"/*.mobileprovision; do
  [ -e "$p" ] || continue
  PLIST=$(security cms -D -i "$p" 2>/dev/null)
  APPID=$(echo "$PLIST" | plutil -extract Entitlements.application-identifier raw - 2>/dev/null || echo "?")
  APS=$(echo "$PLIST" | plutil -extract Entitlements.aps-environment raw - 2>/dev/null || echo "")
  case "$APPID" in
    # Only the APP needs the push entitlement: the activity push token is issued
    # to the app, not to the extension. Requiring it of the widget too would
    # warn for ever about something that is correct.
    *".$WWTB_BUNDLE_ID")
      printf "  %-55s push=%s\n" "$APPID" "${APS:-NONE (required)}"
      [ -n "$APS" ] || PUSH_OK=0
      ;;
    *"$WWTB_BUNDLE_ID".*)
      printf "  %-55s push=%s\n" "$APPID" "${APS:-none (not needed)}"
      ;;
  esac
done

if [ "$PUSH_OK" = "0" ]; then
  cat >&2 <<'WARN'

WARNING: a profile has no aps-environment.

Xcode silently DROPS an entitlement the profile does not grant, so the build
will succeed and the app will run. ActivityKit will then issue no push token,
the worker will have nothing to push to, and the Live Activity will freeze the
moment iOS suspends the app -- with no error at any point.

Enable Push Notifications on the App ID, then re-run this script:

  https://developer.apple.com/account/resources/identifiers/list
  -> your identifier -> Capabilities -> tick Push Notifications -> Save

WARN
  exit 2
fi

echo
echo "Profiles are in place. Next: DEVELOPMENT_TEAM=$DEVELOPMENT_TEAM scripts/build-device.sh --install"
