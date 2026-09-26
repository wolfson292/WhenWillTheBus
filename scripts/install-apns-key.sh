#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Puts the APNs signing key on the Docker host with the right ownership.
#
# The .p8 is read INSIDE the container by uid 1654, and the directory is 0700,
# so it cannot simply be copied in as your own user. This does the two-step:
# copy to a temp path, then move into place as the container's user.
#
# Usage:  scripts/install-apns-key.sh ~/Downloads/AuthKey_ABC123DEFG.p8

set -euo pipefail

# --help, and rejecting arguments rather than ignoring them.
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/_common.sh"
wwtb_help "${BASH_SOURCE[0]}" "$@"

# Takes a path to the .p8. Anything flag-shaped is a mistake, not a filename.
case "${1:-}" in
  -*) wwtb_unknown "${BASH_SOURCE[0]}" "$1" ;;
esac

KEY="${1:-}"
HOST="${WWTB_HOST:-192.168.3.151}"
USER="${WWTB_USER:-scottwolf}"
REMOTE="${WWTB_ROOT:-/docker/whenwillthebus}"

[ -n "$KEY" ] || { echo "usage: $0 <path to AuthKey_XXXXXXXXXX.p8>" >&2; exit 1; }
[ -f "$KEY" ] || { echo "error: no such file: $KEY" >&2; exit 1; }

# Fail early on the wrong file. A PEM that is not a private key signs nothing,
# and the failure surfaces much later as a rejected push.
grep -q "BEGIN PRIVATE KEY" "$KEY" || {
  echo "error: $KEY does not look like an APNs .p8 (no BEGIN PRIVATE KEY)." >&2
  exit 1
}

echo "==> Copying to $HOST"
scp -q "$KEY" "$USER@$HOST:/tmp/wwtb-authkey.p8"

echo "==> Installing as the container's user"
ssh -o BatchMode=yes "$USER@$HOST" "
  sudo install -o 1654 -g 1654 -m 400 /tmp/wwtb-authkey.p8 $REMOTE/secrets/AuthKey.p8 &&
  shred -u /tmp/wwtb-authkey.p8 2>/dev/null || rm -f /tmp/wwtb-authkey.p8
  sudo ls -l $REMOTE/secrets/AuthKey.p8
"

echo
echo "Key in place. Set APNS_KEY_ID in Portainer and update the stack."
