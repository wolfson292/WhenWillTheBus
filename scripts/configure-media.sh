#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Points the worker at Radarr and Sonarr.
#
# Reads their API keys from their own config.xml on the Docker host and writes
# them into the Portainer stack's environment, leaving every other variable
# alone. The keys are never printed and never land in a file in this repository.
#
# Re-runnable: it overwrites the media variables and preserves the rest, so it
# is also how you change a quality profile or a root folder later.
#
# Usage:
#   scripts/configure-media.sh [--profile N] [--redeploy]

set -euo pipefail

# --help, and rejecting arguments rather than ignoring them.
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/_common.sh"
wwtb_help "${BASH_SOURCE[0]}" "$@"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

HOST="${WWTB_HOST:-192.168.3.151}"
USER="${WWTB_USER:-scottwolf}"
STACK_NAME="${WWTB_STACK:-whenwillthebus}"

# 6 is "HD - 720p/1080p" on both instances here. Deliberate rather than
# inherited: Ultra-HD is four to eight times the size, and nobody notices that
# choice until a disk fills.
PROFILE=6
REDEPLOY=0
while [ $# -gt 0 ]; do
  case "$1" in
    --profile) PROFILE="$2"; shift 2 ;;
    --redeploy) REDEPLOY=1; shift ;;
    *) wwtb_unknown "${BASH_SOURCE[0]}" "$1" ;;
  esac
done

SSH=(ssh -o BatchMode=yes -o ConnectTimeout=10 "$USER@$HOST")

echo "==> Reading Radarr and Sonarr configuration from $HOST"

# Read on the host and hand back one line per instance. The key travels in a
# shell variable and is never echoed.
read -r RADARR_BASE RADARR_KEY RADARR_ROOT <<<"$("${SSH[@]}" bash -s <<'REMOTE'
CFG=$(sudo docker inspect radarr --format '{{range .Mounts}}{{if eq .Destination "/config"}}{{.Source}}{{end}}{{end}}')
KEY=$(sudo grep -o '<ApiKey>[^<]*</ApiKey>' "$CFG/config.xml" | sed 's/<[^>]*>//g')
BASEPATH=$(sudo grep -o '<UrlBase>[^<]*</UrlBase>' "$CFG/config.xml" | sed 's/<[^>]*>//g')
ROOT=$(curl -s -m 10 -H "X-Api-Key: $KEY" "http://localhost:7878${BASEPATH}/api/v3/rootfolder" \
  | python3 -c 'import sys,json; d=json.load(sys.stdin); print(d[0]["path"] if d else "")')
echo "http://HOSTIP:7878${BASEPATH} $KEY $ROOT"
REMOTE
)"

read -r SONARR_BASE SONARR_KEY SONARR_ROOT <<<"$("${SSH[@]}" bash -s <<'REMOTE'
CFG=$(sudo docker inspect sonarr --format '{{range .Mounts}}{{if eq .Destination "/config"}}{{.Source}}{{end}}{{end}}')
KEY=$(sudo grep -o '<ApiKey>[^<]*</ApiKey>' "$CFG/config.xml" | sed 's/<[^>]*>//g')
BASEPATH=$(sudo grep -o '<UrlBase>[^<]*</UrlBase>' "$CFG/config.xml" | sed 's/<[^>]*>//g')
ROOT=$(curl -s -m 10 -H "X-Api-Key: $KEY" "http://localhost:8989${BASEPATH}/api/v3/rootfolder" \
  | python3 -c 'import sys,json; d=json.load(sys.stdin); print(d[0]["path"] if d else "")')
echo "http://HOSTIP:8989${BASEPATH} $KEY $ROOT"
REMOTE
)"

# localhost on the host is not localhost inside the worker's container.
RADARR_URL="${RADARR_BASE/HOSTIP/$HOST}"
SONARR_URL="${SONARR_BASE/HOSTIP/$HOST}"

[ -n "$RADARR_KEY" ] && [ -n "$SONARR_KEY" ] || { echo "error: could not read both API keys." >&2; exit 1; }

echo "    radarr $RADARR_URL  root ${RADARR_ROOT:-<none>}  key ${#RADARR_KEY} chars"
echo "    sonarr $SONARR_URL  root ${SONARR_ROOT:-<none>}  key ${#SONARR_KEY} chars"
echo "    quality profile $PROFILE for both"

export RADARR_URL RADARR_KEY RADARR_ROOT SONARR_URL SONARR_KEY SONARR_ROOT PROFILE STACK_NAME
"$ROOT/scripts/portainer-redeploy.sh" --set-media

[ "$REDEPLOY" = "1" ] && "$ROOT/scripts/deploy-server.sh" --redeploy
