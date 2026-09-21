#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Finds the Apple Developer Team ID from the signing certificates on this Mac.
# Sign Xcode into your account first (Xcode > Settings > Accounts).

set -euo pipefail

# The Team ID is the OU field of the certificate subject, NOT the ten-character
# string in the common name. Those look alike and are different values:
#
#   CN=Apple Development: Scott Wolf (U8LC7QR9K5)   <- certificate id
#   OU=7NFHX4U9ME                                   <- TEAM id
#
# Using the wrong one signs fine and then fails at APNs, where a mismatched
# issuer is a rejected push rather than an error you can read.
TEAMS=$(security find-certificate -a -c "Apple Development" -p 2>/dev/null \
  | openssl storeutl -noout -text /dev/stdin 2>/dev/null \
  | grep -oE 'OU *= *[A-Z0-9]{10}' | grep -oE '[A-Z0-9]{10}$' | sort -u || true)

if [ -z "$TEAMS" ]; then
  # Fall back to parsing each certificate individually.
  TEAMS=$(security find-certificate -c "Apple Development" -p 2>/dev/null \
    | openssl x509 -noout -subject 2>/dev/null \
    | tr ',' '\n' | grep -oE 'OU *= *[A-Z0-9]{10}' | grep -oE '[A-Z0-9]{10}$' | sort -u || true)
fi

if [ -z "$TEAMS" ]; then
  echo "No Apple Development certificate found."
  echo
  echo "In Xcode: Settings > Accounts > add your Apple ID, select the team,"
  echo "then 'Manage Certificates...' and + > Apple Development."
  exit 1
fi

# A certificate can exist and still be unusable: if the intermediate that issued
# it is missing or expired, codesign fails with "unable to build chain to
# self-signed root" while the certificate itself looks perfectly fine.
if ! security find-identity -v -p codesigning 2>/dev/null | grep -q "Apple Development"; then
  echo "WARNING: a certificate exists but is NOT usable for signing."
  echo
  echo "Almost always the Apple WWDR intermediate is missing or expired. Check"
  echo "which generation issued yours, and install that one from"
  echo "https://www.apple.com/certificateauthority/ :"
  echo
  echo "  security find-certificate -c 'Apple Development' -p | openssl x509 -noout -issuer"
  echo
fi

echo "Team ID(s):"
echo "$TEAMS" | sed 's/^/  /'
echo
echo "  export DEVELOPMENT_TEAM=$(echo "$TEAMS" | head -1)"
