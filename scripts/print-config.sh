#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Prints the identifiers the app, the widget and the worker must agree on.
# Everything is derived from Directory.Build.props, so they cannot drift.
#
# Usage:
#   scripts/print-config.sh

set -euo pipefail

# --help, and rejecting arguments rather than ignoring them.
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/_help.sh"
wwtb_help "${BASH_SOURCE[0]}" "$@"

# Takes no arguments. Accepting one silently would mean typing something
# meaningful and having it ignored.
[ $# -eq 0 ] || wwtb_unknown "${BASH_SOURCE[0]}" "$1"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

BUNDLE_ID=$(sed -n 's/.*<ApplicationId[^>]*>\([^<]*\)<.*/\1/p' "$ROOT/Directory.Build.props" | head -1)
[ -n "$BUNDLE_ID" ] || { echo "error: no ApplicationId in Directory.Build.props" >&2; exit 1; }

echo "app bundle id        $BUNDLE_ID"
echo "widget bundle id     $BUNDLE_ID.BusWidget"
echo "APNs topic           $BUNDLE_ID.push-type.liveactivity"
echo
echo "worker environment:"
echo "  APNS_BUNDLE_ID=$BUNDLE_ID"
