#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Finds the Apple Developer Team ID from the signing certificates Xcode has
# downloaded. Sign Xcode into your account first (Xcode > Settings > Accounts).

set -euo pipefail

FOUND=$(security find-identity -v -p codesigning 2>/dev/null \
  | grep -oE '\(([A-Z0-9]{10})\)$' | tr -d '()' | sort -u || true)

if [ -z "$FOUND" ]; then
  echo "No signing identities found."
  echo
  echo "In Xcode: Settings > Accounts > add your Apple ID, select the team,"
  echo "then 'Manage Certificates...' and + > Apple Development."
  exit 1
fi

echo "Team ID(s):"
echo "$FOUND" | sed 's/^/  /'
echo
echo "Then: export DEVELOPMENT_TEAM=$(echo "$FOUND" | head -1)"
