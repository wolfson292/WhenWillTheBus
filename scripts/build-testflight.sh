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
#   DEVELOPMENT_TEAM=XXXXXXXXXX scripts/build-testflight.sh
#
# Then upload the .ipa, either with Transporter.app or:
#   xcrun altool --upload-app -f <ipa> -t ios \
#     --apiKey <KEY_ID> --apiIssuer <ISSUER_ID>

set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

[ -n "${DEVELOPMENT_TEAM:-}" ] || { echo "error: DEVELOPMENT_TEAM is not set. Run scripts/team-id.sh." >&2; exit 1; }

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

echo "==> 1/4 ActivityKit bridge"
./scripts/build-ios-native.sh > /dev/null

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
WWTB_DISTRIBUTION=1 ./scripts/build-widget.sh device Release

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
fi

[ "$FAILED" = "0" ] || { echo; echo "error: this package would be rejected. Not uploading it." >&2; exit 1; }

echo
echo "Built: $IPA"
echo
echo "Upload it with Transporter.app, or:"
echo "  xcrun altool --upload-app -f \"$IPA\" -t ios --apiKey <KEY_ID> --apiIssuer <ISSUER_ID>"
echo
echo "The worker needs no change: this build tells it the token is production."
