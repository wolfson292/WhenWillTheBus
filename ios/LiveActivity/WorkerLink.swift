// SPDX-License-Identifier: GPL-3.0-or-later

import Foundation

/// Lets the widget ask the worker for its snapshot when the app has not run.
///
/// The widget used to show only what the app last wrote, and the app writes
/// only while somebody has it open. On a school day nobody does: on 7 Oct the
/// widget had no status all afternoon while the worker knew the rider was
/// aboard and when she would be home. The worker serves the SAME snapshot the
/// app writes, built by the same C# code, at /widget.
///
/// The app leaves the worker's address and key in the App Group as
/// `widget-worker.json`, and removes it when no worker is configured.
enum WorkerLink {
    static let filename = "widget-worker.json"

    private struct Link: Decodable {
        var url: String
        var key: String
    }

    /// How old the app's own snapshot may be before the worker is asked instead.
    ///
    /// The app writes every thirty seconds while it runs, so anything older
    /// than this means it has stopped; asking sooner would only spend the
    /// widget's budget re-fetching what the app just wrote.
    static let freshEnough: TimeInterval = 2 * 60

    /// Fetch the worker's snapshot, and keep it in the App Group so the next
    /// draw has it even without a network. Nil when there is no worker, or it
    /// cannot be reached -- the caller then shows what it already has.
    static func fetch() async -> BusSnapshot? {
        guard
            let container = FileManager.default
                .containerURL(forSecurityApplicationGroupIdentifier: BusSnapshot.appGroup),
            let data = try? Data(contentsOf: container.appendingPathComponent(filename)),
            let link = try? JSONDecoder().decode(Link.self, from: data),
            let url = URL(string: link.url + "/widget")
        else { return nil }

        var request = URLRequest(url: url, timeoutInterval: 10)
        request.setValue("Bearer \(link.key)", forHTTPHeaderField: "Authorization")

        guard
            let (body, response) = try? await URLSession.shared.data(for: request),
            (response as? HTTPURLResponse)?.statusCode == 200,
            let snapshot = try? JSONDecoder().decode(BusSnapshot.self, from: body)
        else { return nil }

        // Written where the app writes, so the widget's other surfaces and its
        // next draw read the same answer.
        if let url = BusSnapshot.url {
            try? body.write(to: url, options: .atomic)
        }

        return snapshot
    }
}
