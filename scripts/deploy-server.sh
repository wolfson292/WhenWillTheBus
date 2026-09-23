#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Ships the worker's source to the Docker host and builds the image THERE.
#
# Building on the target avoids cross-compiling from an arm64 Mac to an amd64
# host, needs no registry and no Git remote, and is faster on a many-core box.
# Portainer then manages the running stack in the ordinary way: environment
# variables, logs, restart, redeploy.
#
# Re-run this whenever the server code changes. --redeploy then does the
# Portainer half too, and verifies the running container is actually on the
# image that was just built -- which is the step everything else hinges on and
# the one that is silent when it does not happen.
#
# Usage:
#   scripts/deploy-server.sh [--redeploy]
#
# Override the target with WWTB_HOST / WWTB_USER / WWTB_ROOT.

set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

REDEPLOY=0
[ "${1:-}" = "--redeploy" ] && REDEPLOY=1

HOST="${WWTB_HOST:-192.168.3.151}"
USER="${WWTB_USER:-scottwolf}"
# This host keeps each app's files under /docker/<app>.
REMOTE="${WWTB_ROOT:-/docker/whenwillthebus}"
TAG="${WWTB_TAG:-whenwillthebus:1.0}"
SSH=(ssh -o BatchMode=yes -o ConnectTimeout=10 "$USER@$HOST")

echo "==> Target $USER@$HOST:$REMOTE"

# The container runs as uid 1654, not root. data must be writable by it or the
# learned history silently fails to persist; secrets must be readable by it.
"${SSH[@]}" "sudo mkdir -p $REMOTE/context $REMOTE/data $REMOTE/secrets \
  && sudo chown -R $USER:$USER $REMOTE/context \
  && sudo chown -R 1654:1654 $REMOTE/data $REMOTE/secrets \
  && sudo chmod 700 $REMOTE/secrets"

# The build context must mirror the repository root, because the Dockerfile
# refers to src/WhenWillTheBus.* -- it is the same file used locally, and having
# it work only in one layout would be a trap for whoever builds next.
echo "==> Syncing source"
rsync -az --delete \
  --exclude 'bin/' --exclude 'obj/' --exclude '.git/' \
  --exclude 'data/' --exclude 'secrets/' --exclude '*.p8' --exclude '.env' \
  --relative \
  ./Directory.Build.props ./src/WhenWillTheBus.Core ./src/WhenWillTheBus.Server \
  "$USER@$HOST:$REMOTE/context/"

echo "==> Building $TAG on $HOST"
"${SSH[@]}" "cd $REMOTE/context && sudo docker build -q -f src/WhenWillTheBus.Server/Dockerfile -t $TAG . " \
  | sed 's/^/    /'

"${SSH[@]}" "sudo docker image inspect $TAG --format '    built {{.Os}}/{{.Architecture}}  {{.Size}} bytes'"

echo
echo "Image is on $HOST."
echo

if [ "$REDEPLOY" = "0" ]; then
  echo "NOW REDEPLOY THE STACK IN PORTAINER -- a plain restart is not enough."
  echo "\"docker restart\" reuses the running container, and a container is bound to"
  echo "the image it was CREATED from, so it keeps serving the old build while"
  echo "reporting healthy. Portainer's Redeploy recreates it."
  echo
  echo "Or re-run this with --redeploy to do it from here."
  exit 0
fi

"$ROOT/scripts/portainer-redeploy.sh"

# VERIFIED, not assumed. Everything about this step fails quietly: a redeploy
# that did not happen leaves a container running the old image and reporting
# healthy, which is indistinguishable from success until a bug you already
# fixed turns up again on a phone.
echo "==> Waiting for the container to come back on the new image"
WANT=$("${SSH[@]}" "sudo docker image inspect $TAG --format '{{.Id}}'")

for attempt in $(seq 1 30); do
  GOT=$("${SSH[@]}" "sudo docker inspect whenwillthebus --format '{{.Image}}'" 2>/dev/null || echo "")
  HEALTH=$("${SSH[@]}" "sudo docker inspect whenwillthebus --format '{{.State.Health.Status}}'" 2>/dev/null || echo "")

  if [ "$GOT" = "$WANT" ] && [ "$HEALTH" = "healthy" ]; then
    echo "    running ${WANT:0:19}  healthy"
    echo
    echo "Redeployed and verified."
    exit 0
  fi

  sleep 2
done

echo >&2
echo "error: the container is not on the new image after 60s." >&2
echo "       wanted ${WANT:0:19}" >&2
echo "       running ${GOT:0:19}  health=${HEALTH:-unknown}" >&2
exit 1
