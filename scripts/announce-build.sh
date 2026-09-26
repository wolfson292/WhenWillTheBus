#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Tells the worker which build everybody should be on, and notifies the phones
# that are not.
#
# build-testflight.sh does this itself after a successful upload. This exists
# for when that half failed -- the worker being unreachable at that moment says
# nothing about the build, and re-uploading to fix it would be absurd.
#
# Usage:
#   scripts/announce-build.sh 202609261030
#   scripts/announce-build.sh 202609261030 --quiet    # record it, tell nobody

set -euo pipefail

# --help, and rejecting arguments rather than ignoring them.
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/_common.sh"
wwtb_help "${BASH_SOURCE[0]}" "$@"

BUILD="${1:-}"
NOTIFY=true

case "${2:-}" in
  "") ;;
  --quiet) NOTIFY=false ;;
  *) wwtb_unknown "${BASH_SOURCE[0]}" "$2" ;;
esac

case "$BUILD" in
  "" | -*) wwtb_unknown "${BASH_SOURCE[0]}" "${BUILD:-<no build number>}" ;;
esac

HOST="${WWTB_HOST:-192.168.3.151}"
USER="${WWTB_USER:-scottwolf}"

echo "==> Announcing $BUILD (notify: $NOTIFY)"

# Run where the admin key already is, rather than copying it to this Mac.
ssh -o BatchMode=yes -o ConnectTimeout=10 "$USER@$HOST" \
  "KEY=\$(sudo docker inspect whenwillthebus --format '{{range .Config.Env}}{{println .}}{{end}}' \
     | sed -n 's/^WWTB_Api__AdminKey=//p'); \
   [ -n \"\$KEY\" ] || { echo 'error: no admin key set on the worker.' >&2; exit 1; }; \
   curl -sS -X POST http://localhost:8471/admin/build \
     -H \"Authorization: Bearer \$KEY\" -H 'Content-Type: application/json' \
     -d '{\"build\":\"$BUILD\",\"notify\":$NOTIFY}'" | sed 's/^/    /'
echo
