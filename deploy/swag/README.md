# Exposing the worker through SWAG

<!-- SPDX-License-Identifier: GPL-3.0-or-later -->

Two files, copied into the SWAG config volume:

| File | Goes to | Why |
|---|---|---|
| `00-whenwillthebus-limits.conf` | `nginx/site-confs/` | http context — defines the rate-limit zone. Named `00-` so it loads before the proxy-conf that uses it. |
| `whenwillthebus.subfolder.conf` | `nginx/proxy-confs/` | the `location` block |

```bash
scp deploy/swag/*.conf user@host:/tmp/
ssh user@host '
  sudo install -m 644 /tmp/00-whenwillthebus-limits.conf /docker/swag/nginx/site-confs/
  sudo install -m 644 /tmp/whenwillthebus.subfolder.conf  /docker/swag/nginx/proxy-confs/
  sudo docker exec swag nginx -t && sudo docker exec swag nginx -s reload'
```

The worker's stack must join the network SWAG is on (`home` here) so nginx can
reach it as `http://whenwillthebus:8080`.

---

## Why this one differs from the other proxy-confs

Every other service here includes `authelia-location.conf`. This one does not,
and that is deliberate: the iOS app authenticates with a bearer token and cannot
complete a browser login flow, so Authelia would simply break it.

This endpoint holds a WheresTheBus password and knows a child's live location, so
two rules stand in for the missing session layer:

**Cloudflare-only.** `nas.denlair.com` is proxied, so real traffic always arrives
from Cloudflare's ranges. Anything else is someone talking directly to the origin
address, which is exactly what to refuse. Verified: a direct request to the
origin returns **403**, while another service on the same proxy still answers —
the rule is scoped, not global.

**Rate limited per real visitor**, keyed on `CF-Connecting-IP` rather than
`$binary_remote_addr`. Every request arrives from a Cloudflare edge, so limiting
on the connecting address would lump unrelated people together and throttle
almost nothing. Answers **429**, not nginx's default 503, which reads as a broken
server and invites retrying harder.

The bearer token is still the primary control; the worker compares it in fixed
time so the endpoint cannot be used to guess it a byte at a time.

## If Cloudflare changes its ranges

The allow-list is a copy of <https://www.cloudflare.com/ips>. If it changes,
legitimate traffic starts returning 403. Re-fetch and reload.

## Verifying

```bash
curl -s -o /dev/null -w '%{http_code}\n' https://nas.denlair.com/whenwillthebus/health          # 200
curl -s -o /dev/null -w '%{http_code}\n' https://nas.denlair.com/whenwillthebus/status          # 401
curl -sk -o /dev/null -w '%{http_code}\n' -H 'Host: nas.denlair.com' \
     https://<origin-ip>:9443/whenwillthebus/health                                             # 403
```
