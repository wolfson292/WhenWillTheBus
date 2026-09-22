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

echo "==> 1/3 ActivityKit bridge"
./scripts/build-ios-native.sh > /dev/null

echo "==> 2/3 Widget extension (production signing)"
APNS_ENVIRONMENT=production ./scripts/build-widget.sh device Release >/dev/null 2>&1 || true

echo "==> 3/3 App archive"
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

echo
echo "Built: $IPA"
echo
echo "Upload it with Transporter.app, or:"
echo "  xcrun altool --upload-app -f \"$IPA\" -t ios --apiKey <KEY_ID> --apiIssuer <ISSUER_ID>"
echo
echo "The worker needs no change: this build tells it the token is production."
