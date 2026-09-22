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
# Usage:  DEVELOPMENT_TEAM=XXXXXXXXXX scripts/provision.sh [--distribution]
#
# --distribution mints the APP STORE profiles a TestFlight build needs instead
# of the development ones. They are a different kind of profile, not a variant:
# they carry aps-environment=production and no get-task-allow, and automatic
# signing only reaches for them when the action is ARCHIVE. Building for a
# device and archiving therefore mint different things from the same stub.

set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

DISTRIBUTION=0
[ "${1:-}" = "--distribution" ] && DISTRIBUTION=1

[ -n "${DEVELOPMENT_TEAM:-}" ] || { echo "error: DEVELOPMENT_TEAM is not set. Run scripts/team-id.sh." >&2; exit 1; }

# An App Store profile cannot be minted without a distribution certificate, and
# the failure it produces names neither. Checked up front, because the archive
# takes minutes to get there.
if [ "$DISTRIBUTION" = "1" ] && ! security find-identity -v -p codesigning | grep -q "Apple Distribution"; then
  cat >&2 <<'CERT'

error: no Apple Distribution certificate in the keychain.

TestFlight builds are signed with a DISTRIBUTION certificate, which is a
different certificate from the Apple Development one used for your own phone.
Only an Account Holder or Admin can create it, so it cannot be scripted.

  Xcode -> Settings -> Accounts -> select the team -> Manage Certificates
  -> + -> Apple Distribution

That creates it and installs it into the keychain in one step. Then re-run
this with --distribution.

CERT
  exit 6
fi

WWTB_BUNDLE_ID=$(sed -n 's/.*<ApplicationId[^>]*>\([^<]*\)<.*/\1/p' Directory.Build.props | head -1)
export WWTB_BUNDLE_ID
export WWTB_TEAM="$DEVELOPMENT_TEAM"

if [ "$DISTRIBUTION" = "1" ]; then
  echo "==> Provisioning $WWTB_BUNDLE_ID and $WWTB_BUNDLE_ID.BusWidget (App Store)"
else
  echo "==> Provisioning $WWTB_BUNDLE_ID and $WWTB_BUNDLE_ID.BusWidget"
fi
xcodegen generate --spec ios/project.yml --project ios >/dev/null

# Target the connected device, not "generic/platform=iOS".
#
# Xcode registers a device with the account as a side effect of building FOR it.
# A generic destination gives it nothing to register, so the profile comes back
# covering only devices already on file -- and the install then fails with
# "This provisioning profile cannot be installed on this device", which does not
# mention registration at all.
DEVICE_UDID=""
[ "$DISTRIBUTION" = "1" ] || DEVICE_UDID=$(xcrun devicectl list devices --json-output /tmp/wwtb-devices.json >/dev/null 2>&1 && python3 -c "
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

if [ "$DISTRIBUTION" = "1" ]; then
  # An App Store profile is not tied to devices at all, so there is nothing to
  # register and no reason to want a phone plugged in.
  DESTINATION=(-destination "generic/platform=iOS")
elif [ -n "$DEVICE_UDID" ]; then
  echo "    registering device $DEVICE_UDID"
  DESTINATION=(-destination "platform=iOS,id=$DEVICE_UDID")
else
  echo "    no device connected -- profiles will cover only devices already registered"
  DESTINATION=(-destination "generic/platform=iOS")
fi

# ARCHIVE, then EXPORT. Neither alone is enough, and the archive is the
# misleading half: it SUCCEEDS while signing with the development profile,
# leaving a green build and no App Store profile anywhere. Automatic signing
# only reaches for a distribution profile when it is asked to export for
# app-store-connect, so that step is what actually mints one.
ARCHIVE="ios/build/prov/ProvisioningHost.xcarchive"
if [ "$DISTRIBUTION" = "1" ]; then
  ACTION=(archive -archivePath "$ARCHIVE")
else
  ACTION=(build)
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
  "${ACTION[@]}" > "$ROOT/ios/build/provision.log" 2>&1 || {
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

if [ "$DISTRIBUTION" = "1" ]; then
  echo "==> Exporting for app-store-connect (this is what mints the profiles)"
  EXPORT_OPTIONS="$ROOT/ios/build/prov/export-options.plist"
  cat > "$EXPORT_OPTIONS" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>method</key><string>app-store-connect</string>
  <key>teamID</key><string>$DEVELOPMENT_TEAM</string>
  <key>signingStyle</key><string>automatic</string>
  <key>destination</key><string>export</string>
  <key>uploadSymbols</key><false/>
</dict>
</plist>
PLIST

  # The stub's own .ipa is thrown away -- only the profiles it causes to be
  # minted are wanted, and the real app is built by dotnet, not by this.
  xcodebuild -exportArchive \
    -archivePath "$ARCHIVE" \
    -exportPath ios/build/prov/export \
    -exportOptionsPlist "$EXPORT_OPTIONS" \
    -allowProvisioningUpdates >> "$ROOT/ios/build/provision.log" 2>&1 || {
      echo "error: export failed. Last lines:" >&2
      grep -iE "error|does not (support|have)|capability" "$ROOT/ios/build/provision.log" | sort -u | tail -10 >&2
      exit 7
    }
fi

echo
DIR="$HOME/Library/Developer/Xcode/UserData/Provisioning Profiles"
# In distribution mode this starts FAILED and must be earned by finding a
# production profile; otherwise it starts passing and is cleared by a profile
# that lacks push.
PUSH_OK=1
[ "$DISTRIBUTION" = "1" ] && PUSH_OK=0
GROUP_OK=1
for p in "$DIR"/*.mobileprovision; do
  [ -e "$p" ] || continue
  PLIST=$(security cms -D -i "$p" 2>/dev/null)
  APPID=$(echo "$PLIST" | plutil -extract Entitlements.application-identifier raw - 2>/dev/null || echo "?")
  APS=$(echo "$PLIST" | plutil -extract Entitlements.aps-environment raw - 2>/dev/null || echo "")
  GRP=$(echo "$PLIST" | plutil -extract Entitlements.com\\.apple\\.security\\.application-groups.0 raw - 2>/dev/null || echo "")
  case "$APPID" in
    # Only the APP needs the push entitlement: the activity push token is issued
    # to the app, not to the extension. Requiring it of the widget too would
    # warn for ever about something that is correct.
    *".$WWTB_BUNDLE_ID")
      printf "  %-55s push=%-14s group=%s\n" "$APPID" "${APS:-NONE (required)}" "${GRP:-NONE (required)}"
      # In distribution mode ONLY a production profile counts. The development
      # one is still sitting in the same directory and its aps-environment is
      # non-empty, so a bare "is there a value" check passes on the wrong
      # profile -- which is how an archive that never minted an App Store
      # profile reported itself as fine.
      if [ "$DISTRIBUTION" = "1" ]; then
        [ "$APS" = "production" ] && PUSH_OK=1
      else
        [ -n "$APS" ] || PUSH_OK=0
      fi
      [ -n "$GRP" ] || GROUP_OK=0
      ;;
    *"$WWTB_BUNDLE_ID".*)
      printf "  %-55s push=%-14s group=%s\n" "$APPID" "${APS:-none (not needed)}" "${GRP:-NONE (required)}"
      [ -n "$GRP" ] || GROUP_OK=0
      ;;
  esac
done

if [ "$GROUP_OK" = "0" ]; then
  cat >&2 <<'GROUPS'

WARNING: a profile does not grant the App Group.

Both the app and the widget must share it, and an entitlement the profile
does not carry is stripped from the build in silence. The widget then finds
no snapshot and shows its placeholder for ever, with nothing to say why.

Enable App Groups on BOTH identifiers, ticking the group under Edit:

  https://developer.apple.com/account/resources/identifiers/list

GROUPS
  exit 5
fi

if [ "$PUSH_OK" = "0" ] && [ "$DISTRIBUTION" = "1" ]; then
  cat >&2 <<'PRODWARN'

WARNING: no App Store profile with aps-environment=production was minted.

The archive can succeed while signing with the DEVELOPMENT profile, which
leaves nothing for a TestFlight build to use. A production entitlement the
profile does not grant is then stripped in silence, and every Live Activity
on every TestFlight phone stops updating with no error anywhere.

Check ios/build/provision.log for the export step.

PRODWARN
  exit 2
fi

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
if [ "$DISTRIBUTION" = "1" ]; then
  echo "App Store profiles are in place."
  echo "Next: DEVELOPMENT_TEAM=$DEVELOPMENT_TEAM scripts/build-testflight.sh"
else
  echo "Profiles are in place. Next: DEVELOPMENT_TEAM=$DEVELOPMENT_TEAM scripts/build-device.sh --install"
fi
