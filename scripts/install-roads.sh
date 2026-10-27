#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Puts a road-snapping engine (OSRM) on the Docker host, for the worker to
# straighten each day's trace onto the roads the bus actually drove.
#
# Fixes come thirty seconds apart, so a raw trace cuts every corner through
# whatever stands on it. OSRM's map matching puts the line back on the road,
# and it runs HERE, on the household's own box, so a child's route never goes
# to anybody's mapping service.
#
# Only the roads around the recorded journeys are fetched, from OpenStreetMap's
# Overpass API: the area the history covers, plus a margin. That is a few
# megabytes and seconds of preprocessing, where all of Florida would need more
# memory than this host has spare. Re-run it if the bus starts going somewhere
# new, or every so often to pick up road changes.
#
# Usage:
#   scripts/install-roads.sh [--margin DEGREES]      # default 0.05, about 3 miles
#
# Override the target with WWTB_HOST / WWTB_USER / WWTB_ROOT, and the published
# port with WWTB_ROADS_PORT (default 5005).

set -euo pipefail

# --help, and rejecting arguments rather than ignoring them.
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/_common.sh"
wwtb_help "${BASH_SOURCE[0]}" "$@"

MARGIN=0.05
while [ $# -gt 0 ]; do
  case "$1" in
    --margin) MARGIN="${2:?--margin takes a number of degrees}"; shift 2 ;;
    *) wwtb_unknown "${BASH_SOURCE[0]}" "$1" ;;
  esac
done

HOST="${WWTB_HOST:-192.168.3.151}"
USER="${WWTB_USER:-scottwolf}"
REMOTE="${WWTB_ROOT:-/docker/whenwillthebus}"
PORT="${WWTB_ROADS_PORT:-5005}"
IMAGE="ghcr.io/project-osrm/osrm-backend:v5.27.1"
ROADS="$REMOTE/roads"
SSH=(ssh -o BatchMode=yes -o ConnectTimeout=10 "$USER@$HOST")

echo "==> Target $USER@$HOST:$ROADS"

# The area the history covers. Read on the host, where the history is, so no
# coordinates pass through this machine or its terminal.
BBOX=$("${SSH[@]}" "sudo -n python3 - $REMOTE/data/history.json $MARGIN" <<'PY'
import json, sys
history = json.load(open(sys.argv[1]))
margin = float(sys.argv[2])
points = [p for rider in history.get("riders", []) for a in rider.get("arrivals", []) for p in a.get("track", [])]
if not points:
    sys.exit("no recorded tracks yet; nothing to cover")
lats = [p[1] for p in points]
lons = [p[2] for p in points]
print(f"{min(lats) - margin:.4f},{min(lons) - margin:.4f},{max(lats) + margin:.4f},{max(lons) + margin:.4f}")
PY
)
echo "==> Covering $BBOX (south,west,north,east)"

"${SSH[@]}" "sudo mkdir -p $ROADS && sudo chown $USER:$USER $ROADS"

# Every road a bus could use, and the turn restrictions that stop OSRM routing
# it through a no-left-turn.
QUERY="[out:xml][timeout:180];(way[\"highway\"]($BBOX);relation[\"type\"=\"restriction\"]($BBOX););(._;>;);out body;"

echo "==> Fetching roads from OpenStreetMap"
"${SSH[@]}" "curl -sS --fail --max-time 300 -A 'WhenWillTheBus/1.0 (household school-bus tracker; one-off road extract)' --data-urlencode 'data=$QUERY' https://overpass-api.de/api/interpreter -o $ROADS/roads.osm.tmp && mv $ROADS/roads.osm.tmp $ROADS/roads.osm && ls -l $ROADS/roads.osm | awk '{print \"    \" \$5 \" bytes\"}'"

echo "==> Preparing for matching"
"${SSH[@]}" "cd $ROADS && rm -f roads.osrm* \
  && sudo docker run --rm -v $ROADS:/data $IMAGE osrm-extract -p /opt/car.lua /data/roads.osm > extract.log 2>&1 \
  && sudo docker run --rm -v $ROADS:/data $IMAGE osrm-partition /data/roads.osrm > partition.log 2>&1 \
  && sudo docker run --rm -v $ROADS:/data $IMAGE osrm-customize /data/roads.osrm > customize.log 2>&1" \
  || { echo "error: preprocessing failed; see $ROADS/*.log on $HOST" >&2; exit 1; }

echo "==> Starting whenwillthebus-roads on port $PORT"
"${SSH[@]}" "sudo docker rm -f whenwillthebus-roads >/dev/null 2>&1 || true; \
  sudo docker run -d --name whenwillthebus-roads --restart unless-stopped \
    -p $PORT:5000 -v $ROADS:/data $IMAGE \
    osrm-routed --algorithm mld --max-matching-size 500 /data/roads.osrm >/dev/null"

# VERIFIED: the middle of the area must snap to a road.
IFS=, read -r SOUTH WEST NORTH EAST <<<"$BBOX"
MIDDLE=$(python3 -c "print(f'{($WEST + $EAST) / 2:.5f},{($SOUTH + $NORTH) / 2:.5f}')")
for attempt in $(seq 1 15); do
  if "${SSH[@]}" "curl -s --max-time 5 'http://127.0.0.1:$PORT/nearest/v1/driving/$MIDDLE' | grep -q '\"code\":\"Ok\"'" 2>/dev/null; then
    echo "    answering"
    echo
    echo "Roads are up at http://$HOST:$PORT. The worker reads WWTB_Roads__Url."
    exit 0
  fi
  sleep 2
done

echo "error: whenwillthebus-roads is not answering; sudo docker logs whenwillthebus-roads on $HOST" >&2
exit 1
