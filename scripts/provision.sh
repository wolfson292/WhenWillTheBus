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

# The widget target defaults to unsigned, which is right for the simulator and
# wrong here: an extension with no profile of its own cannot be embedded in a
# signed app. Overridden on the command line so the default stays simulator-safe.
xcodebuild \
  -project ios/WhenWillTheBusWidget.xcodeproj \
  -scheme ProvisioningHost \
  -sdk iphoneos \
  -configuration Release \
  -destination "generic/platform=iOS" \
  -derivedDataPath ios/build/prov \
  DEVELOPMENT_TEAM="$DEVELOPMENT_TEAM" \
  CODE_SIGN_STYLE=Automatic \
  CODE_SIGNING_ALLOWED=YES \
  CODE_SIGNING_REQUIRED=YES \
  -allowProvisioningUpdates \
  build > "$ROOT/ios/build/provision.log" 2>&1 || {
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
