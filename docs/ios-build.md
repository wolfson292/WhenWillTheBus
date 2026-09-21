# Building the iOS app

<!-- SPDX-License-Identifier: GPL-3.0-or-later -->

Almost all of this app is C#. Two things are not, and both are forced by
WidgetKit rather than chosen:

1. **The Live Activity card's layout** (`ios/LiveActivity/`) — WidgetKit UI must
   be SwiftUI, and a widget extension is a separate process with its own bundle.
2. **Six `@_cdecl` functions** (`ios/App/LiveActivityBridge.swift`) that let C#
   start, update and end an activity.

Everything that decides *what* the card says — the prediction, the stage
machine, the push policy — is C# in `WhenWillTheBus.Core`.

---

## 1. Workloads

```bash
sudo dotnet workload install maui-ios
```

Needed because this machine's .NET lives in a root-owned `/usr/local/share/dotnet`.

## 2. The bridge

```bash
./scripts/build-ios-native.sh
```

Produces `ios/build/WhenWillTheBusNative.xcframework`, which the `.csproj` picks
up automatically via `<NativeReference>`. Verify the symbols survived:

```bash
nm -gU ios/build/device/libWhenWillTheBusNative.a | grep wwtb
```

You should see six `T _wwtb_*` entries. Those names are what
`DllImport("__Internal")` resolves against; if they are missing or mangled, every
bridge call fails at runtime with an `EntryPointNotFoundException`.

## 3. The widget extension

Generated, not hand-built:

```bash
./scripts/build-widget.sh                 # simulator
DEVELOPMENT_TEAM=XXXXXXXXXX ./scripts/build-widget.sh device Release
```

That regenerates `ios/WhenWillTheBusWidget.xcodeproj` from `ios/project.yml`
with XcodeGen, builds `BusWidget.appex`, and embeds it in the MAUI app bundle
under `PlugIns/`.

**This used to be a dozen clicks through Xcode's New Target wizard.** It is a
generated project instead for three reasons: it survives in a diff, it can be
regenerated after any change, and — most importantly — it removes the step
everybody gets wrong.

### The checkbox that is no longer a checkbox

In a hand-built project, `BusActivityAttributes.swift` has to be ticked into
*both* the app target and the widget extension target under Target Membership.
Miss it and the activity starts perfectly, then ignores every update: no error,
no log line, nothing on the phone.

Here the app gets that type through `WhenWillTheBusNative.xcframework` (step 2)
and the extension compiles it from `ios/LiveActivity/`. Both read the same file
from the same path, so they cannot drift apart and there is no checkbox to miss.

### If you would rather use Xcode's UI

You can still open `ios/WhenWillTheBusWidget.xcodeproj` and work in it normally.
Just re-run `xcodegen generate --spec ios/project.yml` afterwards, or edit
`project.yml` instead — regeneration overwrites the project file.

### Verifying it landed

```bash
APP=$(xcrun simctl get_app_container booted com.denlair.whenwillthebus app)
ls "$APP/PlugIns"                                                   # BusWidget.appex
plutil -extract NSExtension.NSExtensionPointIdentifier raw "$APP/PlugIns/BusWidget.appex/Info.plist"
plutil -extract NSSupportsLiveActivities raw "$APP/Info.plist"      # true
```

Two things iOS will not warn you about:

- The directory is **`PlugIns`**, with that exact spelling. An extension anywhere
  else is not an error, it is simply never loaded.
- The extension's bundle id must sit **under** the app's
  (`com.denlair.whenwillthebus.BusWidget` under `com.denlair.whenwillthebus`).
  The build script checks both.

## 4. Push

Live Activity pushes are addressed to `<bundleId>.push-type.liveactivity`, so
`Apns:BundleId` on the worker must equal the app's bundle id — not the widget's.

**A development build registers against Apple's sandbox**, and a token from one
environment is rejected outright by the other. That failure presents as a silent
non-delivery, so set `APNS_SANDBOX=true` in `.env` while running from Xcode and
`false` for TestFlight and the App Store. This is worth getting right before
debugging anything else.

---

## When the card does not appear

In rough order of likelihood:

| Symptom | Cause |
|---|---|
| Nothing starts at all | Live Activities switched off for the app in Settings. `LiveActivityBridge.Enabled` reports this. |
| `EntryPointNotFoundException` | The xcframework was not rebuilt, or `<NativeReference>` did not resolve. Step 2. |
| Card starts, never updates | `BusActivityAttributes` is not a member of both targets, or the C# and Swift content states disagree. Step 3.4. |
| Card updates in the foreground only | The worker is unreachable, or the activity's push token was never registered. Check `/status`. |
| Pushes accepted, nothing shows | Sandbox/production mismatch. Step 4. |
| Card vanishes before the afternoon | Expected: iOS retires an activity roughly eight hours after its last update. A morning card is gone before the afternoon bus leaves school, which is why each journey gets its own activity. |
