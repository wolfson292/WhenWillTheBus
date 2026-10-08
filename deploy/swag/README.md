# Exposing the worker through SWAG

<!-- SPDX-License-Identifier: GPL-3.0-or-later -->

The app reaches the worker at **`https://nas.denlair.com/whenwillthebus/`**,
and that address must stay exactly as it is: every phone is set up with it,
and the app's HTTP client deliberately follows no redirects, so moving it or
redirecting it cuts the app off.

**`https://nas.denlair.com/worker/`** is a second way in, for a browser: the
root sends a person to `/manage`, which asks for the key (any username).

Four files, copied into the SWAG config volume:

| File | Goes to | Why |
|---|---|---|
| `00-whenwillthebus-limits.conf` | `nginx/site-confs/` | http context — defines the rate-limit zone. Named `00-` so it loads before the proxy-confs that use it. |
| `whenwillthebus-location.conf` | `nginx/` | everything about reaching the worker except where it is mounted: the Cloudflare-only rule, the rate limit, the upstream. Included by both locations, so the two cannot drift apart. |
| `whenwillthebus.subfolder.conf` | `nginx/proxy-confs/` | `/whenwillthebus/`, the app's address |
| `worker.subfolder.conf` | `nginx/proxy-confs/` | `/worker/`, for a browser |

```bash
scp deploy/swag/*.conf user@host:/tmp/
ssh user@host '
  sudo install -m 644 /tmp/00-whenwillthebus-limits.conf /docker/swag/nginx/site-confs/
  sudo install -m 644 /tmp/whenwillthebus-location.conf  /docker/swag/nginx/
  sudo install -m 644 /tmp/worker.subfolder.conf          /docker/swag/nginx/proxy-confs/
  sudo install -m 644 /tmp/whenwillthebus.subfolder.conf  /docker/swag/nginx/proxy-confs/
  sudo docker exec swag nginx -t && sudo docker exec swag nginx -s reload'
```

**`nginx -t` passing proves nothing about this order.** Each location includes
the shared file BEFORE its `rewrite ... break`, because `break` ends the
rewrite phase and the `set $upstream_*` lines belong to it. The other way
round is valid config that answers every request with a 500 (`invalid URL
prefix in "://:"`), so check `/worker/health` returns 200 after a reload.

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
the rule is scoped, not global. The check uses the *connecting* address
(`$realip_remote_addr`, via a `geo` block), not `allow`/`deny`, because SWAG
rewrites `$remote_addr` to the visitor (below).

**Rate limited per real visitor**, keyed on `$binary_remote_addr`. That only
works because SWAG restores the visitor's address from `CF-Connecting-IP`, and
trusts that header only from Cloudflare's ranges (`set_real_ip_from` +
`real_ip_header` in the http context). Without that, every request would come
from a Cloudflare edge and the limit would lump unrelated people together; with
it, a forged `CF-Connecting-IP` sent straight to the origin is ignored. Answers
**429**, not nginx's default 503, which reads as a broken server and invites
retrying harder.

The bearer token is still the primary control; the worker compares it in fixed
time so the endpoint cannot be used to guess it a byte at a time.

## If Cloudflare changes its ranges

The `geo` block in `00-whenwillthebus-limits.conf` is a copy of
<https://www.cloudflare.com/ips>, as is SWAG's `set_real_ip_from` list. If the
ranges change, legitimate traffic starts returning 403. Re-fetch, update both,
and reload.

## Verifying

```bash
curl -s -o /dev/null -w '%{http_code}\n' https://nas.denlair.com/whenwillthebus/health          # 200
curl -s -o /dev/null -w '%{http_code}\n' https://nas.denlair.com/whenwillthebus/status          # 401
curl -sk -o /dev/null -w '%{http_code}\n' -H 'Host: nas.denlair.com' \
     https://<origin-ip>:9443/whenwillthebus/health                                             # 403
```
