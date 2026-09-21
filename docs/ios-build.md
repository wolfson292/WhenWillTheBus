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

This is the one step Xcode has to do, and it only has to be done once.

1. Open Xcode → **File → New → Project → iOS → App**, named `WhenWillTheBusHost`,
   bundle id **exactly** the `ApplicationId` in
   `src/WhenWillTheBus.App/WhenWillTheBus.App.csproj`
   (`com.example.whenwillthebus` as written — change both together).
2. **File → New → Target → Widget Extension**, named `BusWidget`. Tick
   **Include Live Activity**. Its bundle id becomes
   `com.example.whenwillthebus.BusWidget`.
3. Delete the generated `BusWidget` Swift files and add instead:
   - `ios/LiveActivity/BusActivityAttributes.swift`
   - `ios/LiveActivity/BusLiveActivity.swift`
4. **`BusActivityAttributes.swift` must be a member of BOTH targets** — the app
   and the widget extension. Select it, open the File Inspector, and tick both
   under *Target Membership*.

   This is the single most important checkbox in this document. The two
   processes exchange this type. A copy that differs in either one is an
   activity that starts perfectly and then ignores every update, with no error,
   no log line, and nothing on the phone.
5. Build the extension (Release, Any iOS Device) and copy the resulting
   `BusWidget.appex` into the MAUI app bundle under `Plugins/`:

   ```bash
   cp -R "$DERIVED_DATA/Build/Products/Release-iphoneos/BusWidget.appex" \
         src/WhenWillTheBus.App/bin/.../WhenWillTheBus.App.app/Plugins/
   ```

6. Add `NSSupportsLiveActivities` to the *extension's* Info.plist as well as the
   app's. It is already in the app's at
   `src/WhenWillTheBus.App/Platforms/iOS/Info.plist`.

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
