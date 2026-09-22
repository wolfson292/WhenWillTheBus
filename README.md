# WhenWillTheBus

<!-- SPDX-License-Identifier: GPL-3.0-or-later -->

A native iOS school-bus tracker in C#, replacing the
[ha-wheresthebus](https://github.com/wolfson292/ha-wheresthebus) Home Assistant
integration. It talks to the WheresTheBus parent API directly, makes its own
arrival predictions, keeps its own history, and drives its own Live Activity.

The prediction algorithm is a faithful port of one that ran daily against a real
school bus from 26 August to 18 September 2026. **Every guard in it exists
because something went wrong without it**, and the reasons are written down in
the code next to the code they justify. Measured accuracy on a recorded
afternoon run: worst case 1.19 minutes out in the last five minutes.

---

## How it is put together

```
WhenWillTheBus.Core      net10.0, no platform dependencies
  Api/                   the WheresTheBus client, roster and scan reading
  Prediction/            route matching, anchor ladder, stage machine, engine
  Notifications/         when a Live Activity is worth updating
  Storage/               learned history, and the Home Assistant import
       |
       +---------------------------+
       |                           |
WhenWillTheBus.App          WhenWillTheBus.Server
  .NET MAUI, iOS              ASP.NET Core worker
  foreground polling          polls every 30s, pushes via APNs
  starts the Live Activity    keeps the card alive while the app is suspended
       |
ios/LiveActivity/           SwiftUI. The ONLY part that cannot be C#.
ios/App/                    ~150 lines of Swift exposing ActivityKit to C#.
```

**One algorithm, two hosts.** The engine is a platform-agnostic library, so the
phone and the worker run byte-for-byte the same prediction. Two implementations
would disagree, and the disagreement would show up as a child on a kerb.

**Why any Swift at all.** WidgetKit UI must be SwiftUI — there is no C# path to
it. So the Live Activity's *layout* is Swift, and everything that decides *what
it says* is C#. The seam is six `@_cdecl` functions.

**Why a worker.** Home Assistant polled every thirty seconds forever; an iOS app
does not run. The approach window wants a position every ~30s for about twenty
minutes, twice a school day, and `BGAppRefreshTask` is minutes-to-hours apart.
The worker does the polling and pushes Live Activity updates, which is the one
channel that reliably reaches a suspended app.

---

## Prerequisites

The iOS workload (installed):

```bash
sudo dotnet workload install maui-ios
```

`WhenWillTheBus.NoApp.slnf` builds the engine, the worker and the tests without
it, which is what CI should use.

### Xcode

**Xcode 26.4** — .NET for iOS `26.4.10259` requires it, and the check is exact
equality on major.minor, not a minimum. 26.5, 26.6 and 27.0 all fail it.

A 27.0-capable SDK pack exists on nuget.org (`27.0.10539-xcode27.0`) but no
workload manifest references it yet, so `dotnet workload update` cannot reach
it — it completes and changes nothing. Re-check with:

```bash
curl -s https://api.nuget.org/v3-flatcontainer/microsoft.net.sdk.ios.manifest-10.0.100/index.json
```

#### If actool fails on a fresh Xcode install

```
actool error : No simulator runtime version from ["23F5054h"] available
              to use with iphonesimulator SDK version 23E252
```

The simulator runtime must match the Xcode, and installing an older Xcode does
not bring one: Apple only serves the newest runtime per major version, so
`xcodebuild -downloadPlatform iOS -buildVersion 26.4` answers "not available".
Get the matching runtime through **Xcodes.app**, then:

```bash
xcrun simctl runtime scan-and-mount
```

That last step is the non-obvious one. A freshly downloaded runtime can sit in
storage as `Ready` but unmounted, where `simctl runtime list` shows it and
`simctl list runtimes` does not — and actool only sees the mounted ones, so it
keeps reporting the old runtime's build number. Restarting CoreSimulator does
not help; `scan-and-mount` does.

---

## Running the worker

### On a remote Docker host, managed by Portainer

```bash
./scripts/deploy-server.sh          # syncs source, builds the image ON that host
```

Building on the target sidesteps cross-compiling from an arm64 Mac, and needs
neither a registry nor a Git remote. Portainer then manages the stack normally —
environment variables, logs, restart, redeploy. After a code change, re-run the
script and hit **Redeploy**.

Host, user and path come from `WWTB_HOST` / `WWTB_USER` / `WWTB_ROOT`.

On the host (this one keeps each app's files under `/docker/<app>`):

```
/docker/whenwillthebus/context/   build context (synced; disposable)
/docker/whenwillthebus/data/      learned history  -> /data         MUST persist
/docker/whenwillthebus/secrets/   AuthKey.p8       -> /run/secrets  read-only
```

`data` and `secrets` are owned by **uid 1654**, the user inside the image. Owned
by anyone else, the worker cannot write, so every arrival it learns is lost on
restart — and nothing says so until the estimates quietly fail to improve.

The secrets *directory* is mounted, not the `.p8` file. Bind-mounting a file
that does not exist yet makes Docker create a **directory** with that name, and
the failure that follows never mentions the mount. Install the key with:

```bash
./scripts/install-apns-key.sh ~/Downloads/AuthKey_XXXXXXXXXX.p8
```

### Locally with compose

```bash
cp .env.example .env    # then fill it in
mkdir -p secrets && cp ~/Downloads/AuthKey_XXXXXXXXXX.p8 secrets/AuthKey.p8
docker compose up -d
```

`.env` and `secrets/` are gitignored and must stay that way. The `.p8` can push
to every device your app is installed on until you revoke it.

Check it:

```bash
curl -s -H "Authorization: Bearer $WWTB_API_KEY" http://localhost:8080/status
```

### The status page

`/manage` is the same information as a page rather than as JSON: what each rider's
prediction is and how it was reached, which phones are talking to the worker, and
which Live Activity cards are registered. Open it in a browser — it accepts HTTP
Basic as well as a bearer token, so a person can reach it. Any username; the
password is the API key.

It shows **no coordinates**, for the same reason `/status` shows none.

A phone appears there once it has introduced itself, which it does when the app
opens with a worker configured. What it sends is bounded by what Apple permits:
its `identifierForVendor` (scoped to this vendor, reissued when the last of our
apps leaves the device), its model, its iOS version, its app version, and a name
its owner typed in Settings. `UIDevice.name` has returned a generic model name
since iOS 16 without an entitlement Apple grants for managed fleets, so the name
can only come from a person. Nothing else identifying is sent: no advertising
identifier, no UDID or serial, no MAC address, no phone number, no account, and
no location.

### Reaching it from outside

Live Activity pushes do NOT need this: those go worker → Apple → phone, all
outbound. What needs inbound access is registering a push token and syncing
history — which matters, because a parent opening the app on cellular during the
school run is exactly when a card gets registered.

[deploy/swag/](deploy/swag/) has a reverse-proxy config that exposes the worker
at a subfolder, restricted to Cloudflare origin IPs and rate limited per real
visitor. See its README for why it skips the session auth every other service
uses, and what stands in for it.

Everything but `/health` needs the key. **This service holds the family's
WheresTheBus password and knows where a child is right now** — keep it on your
own network, and do not expose it to the internet without TLS and a reverse
proxy you trust.

---

## Building and running the app

```bash
./scripts/build-ios-native.sh          # the ActivityKit bridge
dotnet build src/WhenWillTheBus.App
./scripts/build-widget.sh              # the Live Activity card, embedded
xcrun simctl boot "iPhone 17 Pro"
xcrun simctl install "iPhone 17 Pro" \
  src/WhenWillTheBus.App/bin/Debug/net10.0-ios/iossimulator-arm64/WhenWillTheBus.App.app
xcrun simctl launch "iPhone 17 Pro" com.denlair.whenwillthebus
```

The widget extension is generated from `ios/project.yml` by XcodeGen, so there
is no hand-clicked Xcode project to keep in step — see
[docs/ios-build.md](docs/ios-build.md).

Live Activities do not run in the simulator in any useful way, so the card
itself needs a real iPhone: **[docs/device-setup.md](docs/device-setup.md)** is
the eight-step checklist, four steps of which are scripted.

---

## History import

The app is useful on day one instead of in October if you feed it what the old
integration already learned. From the reference repo:

```bash
python3 scripts/export_history.py ~/wtb-arrivals.json --store ~/wheresthebus_arrivals
```

Then **Settings → Import history** in the app.

> **This file contains real route coordinates.** A few miles of turns can be
> matched against a road network and put back on the map, and it is a child's
> daily route to and from school. Keep it on-device: never commit it, never
> attach it to a bug report, never upload it to a service.

---

## Testing

```bash
dotnet test WhenWillTheBus.NoApp.slnf    # 102 tests
python3 scripts/falsify.py               # 22 mechanisms, each deleted to prove its test fails
```

`WhenWillTheBus.NoApp.slnf` is everything that builds without the iOS workloads —
the engine, the worker and the tests. Plain `dotnet build` on the full solution
includes the MAUI app and needs the workload install above.

The falsification run is the important one. Four tests shipped green in the
reference implementation that passed *identically with the mechanism they named
removed* — including a backtest that scored each journey against a history
consisting of that same journey, where every answer was right by construction.

So: `falsify.py` deletes each guard from the source, runs the test that claims to
cover it, and requires the test to **fail**. A test that still passes against
mutilated code is worth less than no test, because it reads like cover. It has
already caught two vacuous tests in this port — one that asserted against the
very constant it was testing, and one whose fixture quantised two different
readings to the same answer.

---

## What is done, and what is not

**Done and verified**

- The full prediction algorithm: route matching, anchor ladder, clock median,
  outlier rejection, publish hysteresis, stage machine, boarding detection.
- The API client — verified against the live service, including the 307 shard
  redirect and forced JSON decoding.
- The worker: polling, prediction, APNs Live Activity push, auth-gated endpoints.
  Built, run, and smoke-tested end to end.
- The Swift widget and bridge: type-checked against the iOS SDK, and the
  xcframework builds for device and simulator with all six symbols exported.
- 102 tests; 22 mechanisms falsified.
- The MAUI app: builds, installs and runs in the simulator. Both screens render
  and navigate.
- The widget extension: builds, and installs inside the app as
  `PlugIns/BusWidget.appex` with the right extension point and a bundle id
  nested under the app's.
- **On a real iPhone** (18 Pro Max, iOS 26): signed with a development profile
  carrying `aps-environment`, installed, and launched. iOS loads the widget
  extension as its own process alongside the app, which is what confirms the
  embedding is right.

**Not yet exercised against real data** — the app has only been run signed out,
so the prediction path is proven by the test suite rather than by a live bus,
and no Live Activity has been rendered yet because none has had a journey to
show. The worker has never pushed to a real device.

**Not started**

- The §6 boarding check from the rider's phone (CoreLocation).
- A map view for the bus marker.
- Adaptive scan polling; the 5-minute interval is known to be too slow around
  afternoon loading.
- The endogenous "pace ratio" for traffic, which the handoff designs but
  deliberately leaves unbuilt.

---

## Licence

GPL-3.0-or-later, inherited from the reference implementation this ports.
