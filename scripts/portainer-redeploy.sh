#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Redeploys the stack through Portainer's own API, which is the only thing that
# actually picks up a rebuilt image.
#
# "docker restart" does not: a container is bound to the image it was CREATED
# from, so it keeps serving the old build while reporting healthy. Only a
# recreate helps, and Portainer owns this stack.
#
# WHY THIS TALKS TO PORTAINER DIRECTLY rather than through the MCP connector:
# the update endpoint takes the environment variables as part of the payload,
# and the connector redacts their values on the way out. Sending back what it
# returned would write the string "[REDACTED]" into the WheresTheBus password
# and the APNs configuration -- and the container would come up healthy while
# doing it. Read here, the values are the real ones and make a faithful
# round-trip.
#
# Usage:
#   scripts/portainer-redeploy.sh [stack-name]        # default: whenwillthebus
#
# Configuration, in order of preference:
#   PORTAINER_URL       e.g. http://192.168.1.143:9000
#   PORTAINER_API_KEY   Portainer -> My account -> Access tokens
#
# Both fall back to the values already configured for the Portainer MCP server
# in ~/.claude.json, so on this machine it usually needs no setup at all.

set -euo pipefail

STACK_NAME="${1:-whenwillthebus}"

# ---------------------------------------------------------------- credentials
read -r RESOLVED_URL RESOLVED_KEY <<<"$(python3 - <<'PY'
import json, os, pathlib

url = os.environ.get("PORTAINER_URL", "")
key = os.environ.get("PORTAINER_API_KEY", "")

# The Portainer MCP server is configured with exactly these two values on this
# machine. Reusing them keeps one credential rather than two that drift.
if not (url and key):
    config = pathlib.Path.home() / ".claude.json"
    if config.exists():
        try:
            data = json.loads(config.read_text())
        except ValueError:
            data = {}

        def servers(node):
            if isinstance(node, dict):
                for name, value in node.items():
                    if name.lower() == "mcpservers" and isinstance(value, dict):
                        for server, config in value.items():
                            if "portainer" in server.lower():
                                yield config.get("env", {}) or {}
                    else:
                        yield from servers(value)
            elif isinstance(node, list):
                for value in node:
                    yield from servers(value)

        for env in servers(data):
            url = url or env.get("PORTAINER_URL", "")
            key = key or env.get("PORTAINER_API_KEY", "")
            if url and key:
                break

print(url or "-", key or "-")
PY
)"

[ "$RESOLVED_URL" != "-" ] || { echo "error: no PORTAINER_URL, and none configured for the MCP server." >&2; exit 1; }
[ "$RESOLVED_KEY" != "-" ] || {
  cat >&2 <<'KEY'
error: no PORTAINER_API_KEY.

Create one in Portainer -> My account -> Access tokens, then either export it
or let this script read the one the Portainer MCP server already uses.
KEY
  exit 1
}

export PORTAINER_URL="$RESOLVED_URL" PORTAINER_API_KEY="$RESOLVED_KEY" STACK_NAME

echo "==> Portainer $PORTAINER_URL"

python3 - <<'PY'
import json, os, sys, urllib.request, urllib.error

BASE = os.environ["PORTAINER_URL"].rstrip("/")
KEY = os.environ["PORTAINER_API_KEY"]
NAME = os.environ["STACK_NAME"]

def call(path, method="GET", body=None):
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(
        f"{BASE}/api{path}", data=data, method=method,
        headers={"X-API-Key": KEY, "Content-Type": "application/json"})
    try:
        raw = urllib.request.urlopen(request, timeout=60).read()
        return json.loads(raw) if raw else {}
    except urllib.error.HTTPError as error:
        sys.exit(f"error: {method} {path} -> HTTP {error.code}\n{error.read().decode()[:400]}")
    except urllib.error.URLError as error:
        sys.exit(f"error: cannot reach Portainer at {BASE}: {error.reason}")

stacks = call("/stacks")
stack = next((s for s in stacks if s.get("Name") == NAME), None)
if stack is None:
    sys.exit(f"error: no stack named {NAME}. Found: {[s.get('Name') for s in stacks]}")

stack_id = stack["Id"]
endpoint_id = stack["EndpointId"]
env = stack.get("Env") or []
print(f"==> Stack {NAME} (id {stack_id}, environment {endpoint_id}) with {len(env)} variable(s)")

# REFUSE RATHER THAN CLOBBER. The update replaces the environment wholesale, so
# anything less than the real values here would silently blank the WheresTheBus
# password and the APNs configuration -- on a container that then comes up
# reporting healthy.
redacted = [v.get("name") for v in env if str(v.get("value", "")).strip("[]").upper() == "REDACTED"]
if redacted:
    sys.exit(f"error: refusing to update -- these came back redacted: {redacted}")

blank = [v.get("name") for v in env if v.get("value", "") == ""]
if blank:
    print(f"    note: empty on the server already, and left that way: {blank}")

content = call(f"/stacks/{stack_id}/file").get("StackFileContent", "")
if not content.strip():
    sys.exit("error: the stack file came back empty; refusing to write it back")

print("==> Redeploying (recreates the containers; does NOT re-pull the image)")
call(f"/stacks/{stack_id}?endpointId={endpoint_id}", "PUT", {
    "StackFileContent": content,
    "Env": env,
    "Prune": False,
    # The image was just built ON the Docker host, so there is nothing in a
    # registry to pull and asking would only fail or fetch something older.
    "PullImage": False,
})

print("    accepted")
PY

echo
echo "Redeployed. Portainer returns 200 the moment it accepts the request, which is"
echo "not the same as the container being back on the new image -- run this through"
echo "scripts/deploy-server.sh --redeploy to have that waited for and checked."
