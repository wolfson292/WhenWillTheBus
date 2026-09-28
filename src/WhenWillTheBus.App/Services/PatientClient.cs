// SPDX-License-Identifier: GPL-3.0-or-later

namespace WhenWillTheBus.App.Services;

/// <summary>
/// An HttpClient for the calls that take a long time on purpose.
/// </summary>
/// <remarks>
/// A SEPARATE CLIENT BECAUSE HttpClient.Timeout IS NOT NEGOTIABLE PER CALL. It
/// applies on top of any cancellation token and fires first, so the shared
/// client's thirty seconds silently capped a three-minute budget and every
/// suggestion died before it could arrive — reported as "it took too long",
/// which was true and useless.
///
/// The shared client keeps its thirty seconds, which is right for everything
/// else: a poll that hangs for three minutes is a poll that has failed. This
/// one has no deadline of its own and every caller sets a token instead, which
/// is the only way to have two different answers to "how long is too long".
/// </remarks>
public sealed class PatientClient(HttpClient http)
{
    public HttpClient Http { get; } = http;

    public static PatientClient Create() =>
        new(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            // Deliberately none. Every call through here passes a token with
            // its own budget, and a client-wide deadline would override it.
            Timeout = Timeout.InfiniteTimeSpan,
        });
}
