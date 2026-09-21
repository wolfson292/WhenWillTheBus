// SPDX-License-Identifier: GPL-3.0-or-later
//
// A stub app that exists only so Xcode will mint provisioning profiles.
//
// The real app is .NET MAUI, which is not an Xcode project, so `xcodebuild
// -allowProvisioningUpdates` has nothing to act on. This target carries the
// SAME bundle identifier and the SAME entitlements as the MAUI app and embeds
// the same widget extension, so provisioning it provisions exactly what the
// real build needs. It is never installed on anything.

import SwiftUI

@main
struct ProvisioningHostApp: App {
    var body: some Scene {
        WindowGroup {
            Text("Provisioning stub — not the real app.")
                .padding()
        }
    }
}
