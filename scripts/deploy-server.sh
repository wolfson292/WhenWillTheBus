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
# Re-run this whenever the server code changes, then hit Redeploy in Portainer
# (or pass --restart to do it from here).
#
# Usage:
#   scripts/deploy-server.sh [--restart]
#
# Override the target with WWTB_HOST / WWTB_USER / WWTB_ROOT.

set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

HOST="${WWTB_HOST:-192.168.3.151}"
USER="${WWTB_USER:-scottwolf}"
REMOTE="${WWTB_ROOT:-/opt/whenwillthebus}"
TAG="${WWTB_TAG:-whenwillthebus:1.0}"
SSH=(ssh -o BatchMode=yes -o ConnectTimeout=10 "$USER@$HOST")

echo "==> Target $USER@$HOST:$REMOTE"

"${SSH[@]}" "sudo mkdir -p $REMOTE/context $REMOTE/data $REMOTE/secrets \
  && sudo chown -R $USER:$USER $REMOTE && chmod 700 $REMOTE/secrets"

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

if [ "${1:-}" = "--restart" ]; then
  echo "==> Restarting the stack"
  "${SSH[@]}" "sudo docker restart whenwillthebus 2>/dev/null || echo '    (not running yet -- deploy the stack in Portainer)'"
fi

echo
echo "Image is on $HOST. Deploy or redeploy the stack in Portainer."
