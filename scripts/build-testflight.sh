#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Builds a signed .ipa for TestFlight.
#
# A TestFlight build is a PRODUCTION build: it uses production APNs, so the
# entitlement must say so. A development entitlement paired with a TestFlight
# build gives push tokens the production host rejects, silently -- so this
# script sets it explicitly rather than inheriting whatever was last used.
#
# The worker serves both at once: each phone declares its environment when it
# registers, so a TestFlight family and a development phone coexist without
# touching APNS_SANDBOX.
#
# Usage:
#   DEVELOPMENT_TEAM=XXXXXXXXXX scripts/build-testflight.sh                 # build only
#   DEVELOPMENT_TEAM=XXXXXXXXXX scripts/build-testflight.sh --upload        # build, then upload
#   DEVELOPMENT_TEAM=XXXXXXXXXX scripts/build-testflight.sh --upload-only   # upload what is built
#
# --upload-only exists because the two halves cost wildly different amounts and
# it is the cheap one that fails: rebuilding forty minutes of AOT to retry a
# ninety-second transfer is the wrong shape.

set -euo pipefail

# --help, and rejecting arguments rather than ignoring them.
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/_help.sh"
wwtb_help "${BASH_SOURCE[0]}" "$@"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

UPLOAD=0
ONLY_UPLOAD=0
case "${1:-}" in
  "") ;;
  --upload) UPLOAD=1 ;;
  # Uploading is the cheap half and the half that fails. Rebuilding forty
  # minutes of AOT to retry a ninety-second transfer is the wrong shape.
  --upload-only) UPLOAD=1; ONLY_UPLOAD=1 ;;
  *) wwtb_unknown "${BASH_SOURCE[0]}" "$1" ;;
esac

[ -n "${DEVELOPMENT_TEAM:-}" ] || { echo "error: DEVELOPMENT_TEAM is not set. Run scripts/team-id.sh." >&2; exit 1; }

# NEITHER OF THESE IS A SECRET. They name which key to use; the key itself is
# the .p8, which lives in keys/ (gitignored) and at
# ~/.appstoreconnect/private_keys/AuthKey_<KEY_ID>.p8, where altool looks for it.
# Committing the identifiers and not the key is the point: the pair is useless
# without it, and hardcoding nothing means guessing them at 11pm in a term-time
# panic when a build has expired.
APPSTORE_KEY_ID="${APPSTORE_KEY_ID:-3CLNXCUTZD}"
APPSTORE_ISSUER_ID="${APPSTORE_ISSUER_ID:-513a9d41-1a99-496f-bf7c-41e6712d15cb}"

# altool EXITS 0 ON A FAILED UPLOAD. On 23 Sep it printed
#
#   Error: The file doesn't exist. 'Defaults.properties' couldn't be opened
#
# and returned success, so the script reported a build that had reached
# App Store Connect when nothing had. The only trustworthy signal is the
# words it prints, so that is what is checked.
#
# That particular failure is its bundled transporter re-downloading itself and
# is transient, which is why one retry is worth more here than an error would
# be -- the second attempt succeeded immediately.
upload() {
  local ipa="$1" attempt output

  local keyfile="$HOME/.appstoreconnect/private_keys/AuthKey_$APPSTORE_KEY_ID.p8"
  [ -f "$keyfile" ] || {
    echo "error: altool cannot find the App Store Connect key." >&2
    echo "       Expected: $keyfile" >&2
    echo "       Copy it there from keys/AuthKey_$APPSTORE_KEY_ID.p8" >&2
    exit 1
  }

  for attempt in 1 2; do
    echo "==> Uploading $(basename "$ipa") to App Store Connect (attempt $attempt)"
    output=$(xcrun altool --upload-app -f "$ipa" -t ios \
      --apiKey "$APPSTORE_KEY_ID" --apiIssuer "$APPSTORE_ISSUER_ID" 2>&1 || true)

    if grep -q "UPLOAD SUCCEEDED" <<<"$output"; then
      grep -E "Delivery UUID|Transferred" <<<"$output" | sed 's/^/    /'
      echo
      echo "Uploaded. App Store Connect processes it for a few minutes before it"
      echo "appears in TestFlight; internal testers need no review."
      return 0
    fi

    echo "    upload did not report success:" >&2
    grep -iE "error|warning" <<<"$output" | head -5 | sed 's/^/    /' >&2
    [ "$attempt" = "1" ] && echo "    retrying once..." >&2
  done

  echo >&2
  echo "error: the upload failed. The .ipa is fine -- retry just the transfer with:" >&2
  echo "       DEVELOPMENT_TEAM=$DEVELOPMENT_TEAM scripts/build-testflight.sh --upload-only" >&2
  exit 1
}

BUNDLE_ID=$(sed -n 's/.*<ApplicationId[^>]*>\([^<]*\)<.*/\1/p' Directory.Build.props | head -1)
[ "$BUNDLE_ID" != "com.example.whenwillthebus" ] || {
  echo "error: the bundle id is still the placeholder." >&2; exit 1; }

# Bump this for every upload. App Store Connect refuses a build number it has
# already seen, and finding that out after a ten-minute upload is a poor way to
# spend an evening.
BUILD_NUMBER="${BUILD_NUMBER:-$(date +%Y%m%d%H%M)}"

echo "==> Bundle id     $BUNDLE_ID"
echo "==> Team          $DEVELOPMENT_TEAM"
echo "==> Build number  $BUILD_NUMBER"
echo "==> APNs          production"

if [ "$ONLY_UPLOAD" = "1" ]; then
  IPA=$(find src/WhenWillTheBus.App/bin/Release/net10.0-ios -name "*.ipa" | head -1)
  [ -n "$IPA" ] || { echo "error: no .ipa to upload. Build one first." >&2; exit 1; }
  upload "$IPA"
  exit 0
fi

echo "==> 1/4 ActivityKit bridge"
./scripts/build-ios-native.sh > /dev/null

# A TestFlight build must CLAIM production, not just be entitled to it.
#
# -p:ApnsEnvironment=production picks the production entitlement, so the token
# is minted in production. What the app then TELLS the worker comes from
# #if APNS_PRODUCTION (ServerLink, DeviceIdentity), which needs the constant to
# be defined as well. That wiring lived only in the .csproj and was once missing
# entirely: every TestFlight build reported "sandbox", the worker pushed real
# tokens to api.sandbox.push.apple.com, and Apple dropped them without a word.
#
# Asked here because there is no other moment it gets asked. Nothing fails, no
# log line appears, and the only symptom is a card that never moves on somebody
# else's phone.
echo "==> Checking the build will claim production"
CONSTANTS=$(dotnet build src/WhenWillTheBus.App \
  -c Release -p:RuntimeIdentifier=ios-arm64 -p:ApnsEnvironment=production \
  --getProperty:DefineConstants 2>/dev/null)

case "$CONSTANTS" in
  *APNS_PRODUCTION*) echo "    APNS_PRODUCTION defined" ;;
  *)
    echo "error: APNS_PRODUCTION is not defined for a production build." >&2
    echo "       The app would register its push tokens as 'sandbox' and the" >&2
    echo "       worker would push them to the sandbox host, which Apple" >&2
    echo "       silently drops. See DefineConstants in" >&2
    echo "       src/WhenWillTheBus.App/WhenWillTheBus.App.csproj." >&2
    exit 1
    ;;
esac

# THE APP BUNDLE FIRST, then the widget into it, then the package. The widget
# step embeds into an existing .app and fails if there is not one -- so running
# it before anything had been built worked only while a bundle was lying around
# from a previous run. On a clean tree it failed, the failure was swallowed, and
# the .ipa shipped with no widget in it at all and nothing said so.
echo "==> 2/4 App bundle"
dotnet build src/WhenWillTheBus.App \
  -c Release \
  -f net10.0-ios \
  -p:RuntimeIdentifier=ios-arm64 \
  -p:ApnsEnvironment=production \
  -p:ApplicationVersion="$BUILD_NUMBER" \
  -p:CodesignKey="Apple Distribution" \
  -p:CodesignTeam="$DEVELOPMENT_TEAM" \
  -v q --nologo

# Distribution signing, and NOT silenced. An extension signed for development
# inside an App Store build is rejected at upload with ITMS-90035 -- ten minutes
# in, on an error that names neither the extension nor the certificate.
echo "==> 3/4 Widget extension (distribution signing)"
WWTB_DISTRIBUTION=1 WWTB_BUILD="$BUILD_NUMBER" ./scripts/build-widget.sh device Release

echo "==> 4/4 App archive"
dotnet publish src/WhenWillTheBus.App \
  -c Release \
  -f net10.0-ios \
  -p:RuntimeIdentifier=ios-arm64 \
  -p:ApnsEnvironment=production \
  -p:ApplicationVersion="$BUILD_NUMBER" \
  -p:ArchiveOnBuild=true \
  -p:CodesignKey="Apple Distribution" \
  -p:CodesignProvision="Automatic" \
  -p:CodesignTeam="$DEVELOPMENT_TEAM" \
  -v q --nologo

IPA=$(find src/WhenWillTheBus.App/bin/Release/net10.0-ios -name "*.ipa" | head -1)
[ -n "$IPA" ] || { echo "error: no .ipa was produced" >&2; exit 1; }

# VERIFIED, not assumed. Every way this build goes wrong goes wrong SILENTLY and
# is only reported by App Store Connect minutes after an upload -- so the same
# questions are asked here, where an answer costs a second.
echo "==> Verifying the package"
VERIFY=$(mktemp -d)
trap 'rm -rf "$VERIFY"' EXIT
unzip -q "$IPA" -d "$VERIFY"
APP_DIR=$(find "$VERIFY/Payload" -maxdepth 1 -name "*.app" | head -1)
FAILED=0

check() {
  local what="$1" got="$2" want="$3"
  if [ "$got" = "$want" ]; then
    printf "    ok    %-34s %s\n" "$what" "$got"
  else
    printf "    FAIL  %-34s %s (expected %s)\n" "$what" "${got:-<none>}" "$want"
    FAILED=1
  fi
}

# plutil reads a dot in a keypath as NESTING, and these entitlement keys are
# full of dots -- so an unescaped "com.apple.security.application-groups" asks
# for a key "com" containing a key "apple", finds nothing, and reports the
# entitlement missing on a build that carries it perfectly well.
ent() { codesign -d --entitlements :- "$1" 2>/dev/null | plutil -extract "$2" raw - 2>/dev/null; }
GROUPS_KEY='com\.apple\.security\.application-groups.0'
signer() { codesign -dv --verbose=2 "$1" 2>&1 | sed -n 's/^Authority=\(Apple [A-Za-z]*\).*/\1/p' | head -1; }

check "app signed by"        "$(signer "$APP_DIR")"                          "Apple Distribution"
check "app aps-environment"  "$(ent "$APP_DIR" aps-environment)"             "production"
check "app get-task-allow"   "$(ent "$APP_DIR" get-task-allow)"              "false"
check "app app group"        "$(ent "$APP_DIR" "$GROUPS_KEY")"               "group.$BUNDLE_ID"

# An extension is not optional here: without it there is no Live Activity and no
# home-screen widget, and the app installs and runs perfectly well without one.
APPEX="$APP_DIR/PlugIns/BusWidget.appex"
if [ ! -d "$APPEX" ]; then
  printf "    FAIL  %-34s <missing>\n" "widget extension"
  FAILED=1
else
  check "widget signed by"      "$(signer "$APPEX")"                          "Apple Distribution"
  check "widget get-task-allow" "$(ent "$APPEX" get-task-allow)"              "false"
  check "widget app group"      "$(ent "$APPEX" "$GROUPS_KEY")"               "group.$BUNDLE_ID"
  check "widget build number"   "$(plutil -extract CFBundleVersion raw "$APPEX/Info.plist" 2>/dev/null)" \
                                "$BUILD_NUMBER"
fi

[ "$FAILED" = "0" ] || { echo; echo "error: this package would be rejected. Not uploading it." >&2; exit 1; }

echo
echo "Built: $IPA"
echo

if [ "$UPLOAD" = "0" ]; then
  echo "Upload it with Transporter.app, or re-run with --upload, or:"
  echo "  xcrun altool --upload-app -f \"$IPA\" -t ios \\"
  echo "    --apiKey $APPSTORE_KEY_ID --apiIssuer $APPSTORE_ISSUER_ID"
else
  KEYFILE="$HOME/.appstoreconnect/private_keys/AuthKey_$APPSTORE_KEY_ID.p8"
  [ -f "$KEYFILE" ] || {
    echo "error: altool cannot find the App Store Connect key." >&2
    echo "       Expected: $KEYFILE" >&2
    echo "       Copy it there from keys/AuthKey_$APPSTORE_KEY_ID.p8" >&2
    exit 1
  }

  upload "$IPA"
fi

echo
echo "The worker needs no change: this build tells it the token is production."
