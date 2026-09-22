# Getting it onto a real iPhone

<!-- SPDX-License-Identifier: GPL-3.0-or-later -->

A Live Activity cannot meaningfully run in the simulator, so this is the only
way to see the card at all. Eight steps: four are yours, four are scripted.

---

## 1. Sign Xcode into your developer account — **yours**

Xcode → **Settings → Accounts** → **+** → Apple ID. Select the team, then
**Manage Certificates… → + → Apple Development**.

Check it worked:

```bash
./scripts/team-id.sh
```

That prints your ten-character Team ID. Keep it — the worker needs the same one.

## 2. Choose a bundle identifier — **yours**

Apple will not sign `com.example.*`. Edit **one** line, in
`Directory.Build.props`:

```xml
<ApplicationId>com.yourdomain.whenwillthebus</ApplicationId>
```

Everything derives from it — the app, the widget extension, and the APNs topic
the worker pushes to. Confirm with:

```bash
./scripts/print-config.sh
```

This is deliberately one value. A mismatch between any two of those three does
not fail loudly: a wrong APNs topic is a push Apple *accepts* and silently
drops.

## 3. Create an APNs key — **yours**

[developer.apple.com](https://developer.apple.com/account/resources/authkeys/list)
→ **Keys** → **+** → tick **Apple Push Notifications service (APNs)**.

Download the `.p8` and put it at `secrets/AuthKey.p8`. **Apple lets you download
it exactly once.** Note the **Key ID** shown beside it.

> That file can push to every device your app is installed on until you revoke
> it. `secrets/` and `*.p8` are gitignored and must stay that way.

Also check your App ID has the **Push Notifications** capability. Automatic
signing usually adds it when it sees the `aps-environment` entitlement the app
already carries, but it is worth confirming — without it ActivityKit never issues
a push token, and the card simply freezes the moment iOS suspends the app.

## 4. Prepare the phone — **yours**

- Settings → Privacy & Security → **Developer Mode** → on, then reboot.
- Plug it in, unlock it, and tap **Trust** if asked.

```bash
xcrun devicectl list devices      # should list the iPhone, not "simulated"
```

## 5. Build and install — **scripted**

```bash
export DEVELOPMENT_TEAM=XXXXXXXXXX        # from step 1
./scripts/build-device.sh --install
```

That builds the ActivityKit bridge, the signed app, and the widget extension,
embeds the extension, checks the bundle ids nest and that
`NSSupportsLiveActivities` is set, then installs.

## 6. Trust the developer on the phone — **yours**

Settings → General → **VPN & Device Management** → your team → **Trust**.

Then launch the app once and make sure **Live Activities** is on for it in
Settings. A refusal there is the commonest reason a card never appears, and the
app reports it rather than failing silently.

## 7. Run the worker — **scripted**

```bash
cp .env.example .env
./scripts/print-config.sh                 # gives you APNS_BUNDLE_ID
```

Fill in `.env`.

**One worker serves both environments at once.** A development build registers
against Apple's sandbox and a TestFlight build against production; a token from
one is rejected outright by the other, always as a silent non-delivery. That
would make a household with a development phone and TestFlight on everyone
else's impossible to serve — so the environment is chosen **per activity**, not
once for the worker. The app reports which one it belongs to when it registers,
and `ApnsOptions.HostFor` picks the host from that. The signing key is the same
either way; only the host differs.

```
APNS_SANDBOX=true
```

This is now only the **fallback** for a registration that did not say — which
means an app built before the field existed. It does not override an activity
that reported its own environment, so leaving it `true` does not break
TestFlight and leaving it `false` does not break a development phone.

What the app claims comes from `APNS_PRODUCTION`, defined in the `.csproj`
alongside the entitlement, so which environment the token is *minted* in and
which one the app *claims* are decided in the same place. `build-testflight.sh`
refuses to package a build where they disagree.

`/manage` lists every phone and card with the environment it registered under —
the quickest way to confirm a TestFlight install really did say `production`.

```bash
docker compose up -d
curl -s -H "Authorization: Bearer $WWTB_API_KEY" http://localhost:8080/status
```

## 8. Sign in and import history — **yours**

In the app: **Settings** → WheresTheBus email and password, the worker's URL and
API key, then **Import history**.

Import before the first school run. Without it every estimate is the published
timetable, and the afternoon timetable here reads 17:48 against a real arrival
around 17:20.

---

## When it does not work

| Symptom | Look at |
|---|---|
| `No valid iOS code signing keys` | Step 1 — no Apple Development certificate |
| Apple refuses to sign | Step 2 — still `com.example.*` |
| App installs, immediately quits | Step 6 — developer not trusted |
| Card never starts | Live Activities off for the app in Settings |
| Card starts, freezes when backgrounded | No push token: check the Push Notifications capability on the App ID |
| Worker says pushes succeed, nothing appears | Sandbox/production mismatch — step 7 |

Before blaming any of that, ask Apple directly:

```bash
curl -s -X POST -H "Authorization: Bearer $WWTB_API_KEY" http://<worker>/apns/check
```

`$WWTB_API_KEY` is the **worker's** bearer token — the same value the app wants
in its "Worker API key" field. Not the APNs `.p8`, and not its Key ID.

It pushes to a token that cannot exist and reports what Apple says about
everything else. `BadDeviceToken` is the healthy answer: the key, the team and
the topic were all accepted and only the fake token was refused. Any other
reason names the part that is actually wrong. This works on a holiday, at
midnight, and in August — the natural trigger is a school run, which is a
terrible feedback loop for a configuration error.

**It checks BOTH environments**, and `healthy` is true only when both pass:

```json
{"healthy":true,
 "environments":[{"environment":"sandbox","healthy":true,"reason":"BadDeviceToken"},
                 {"environment":"production","healthy":true,"reason":"BadDeviceToken"}]}
```

Checking only one would prove the half belonging to whoever ran the command and
leave the other half — the half on everyone else's phone — unverified until a
school run quietly produced no card.
| Card vanishes before the afternoon | Expected: iOS retires an activity ~8 hours after its last update |
