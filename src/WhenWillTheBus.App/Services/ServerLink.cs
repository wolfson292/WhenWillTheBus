// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net.Http.Headers;
using System.Text;

namespace WhenWillTheBus.App.Services;

/// <summary>
/// Tells the always-on worker which Live Activity to keep up to date.
/// </summary>
/// <remarks>
/// The app starts the activity locally and immediately has a push token for it.
/// Registering that token here is what lets the card keep moving once iOS
/// suspends the app — which it will, within seconds of the phone going in a
/// pocket, and for most of the twenty minutes that actually matter.
/// </remarks>
public sealed class ServerLink(HttpClient http, CredentialStore credentials)
{
    /// <summary>Register an activity's push token. Safe to call repeatedly.</summary>
    public async Task<bool> RegisterAsync(
        string journeyId,
        long childId,
        string pushToken,
        CancellationToken cancellationToken = default)
    {
        (string Url, string Key)? server = await credentials.ReadServerAsync();
        if (server is null)
        {
            // No worker configured. The app still works in the foreground; the
            // card simply stops updating once iOS suspends it.
            return false;
        }

        string body = $$"""
            {"journeyId":"{{journeyId}}","childId":{{childId}},"pushToken":"{{pushToken}}"}
            """;

        using HttpRequestMessage request = new(HttpMethod.Post, $"{server.Value.Url}/activities")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.Value.Key);

        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            // The worker is usually on the home network and the phone is often
            // not. Failing quietly is right: this is an optimisation, not the
            // thing the app depends on to be correct.
            return false;
        }
    }

    /// <summary>Fetch the history the worker has learned, or null if unavailable.</summary>
    /// <remarks>
    /// The worker is always on and the phone is not, so this is how the phone
    /// catches up on runs it never saw. Merging is safe: the engine keys an
    /// arrival by run and date and keeps whichever record knows more.
    /// </remarks>
    public async Task<string?> FetchHistoryAsync(CancellationToken cancellationToken = default)
    {
        (string Url, string Key)? server = await credentials.ReadServerAsync();
        if (server is null)
        {
            return null;
        }

        using HttpRequestMessage request = new(HttpMethod.Get, $"{server.Value.Url}/history/export");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.Value.Key);

        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync(cancellationToken)
                : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>Fetch the view the worker computed, or null if unreachable.</summary>
    public async Task<string?> FetchStateAsync(CancellationToken cancellationToken = default)
    {
        (string Url, string Key)? server = await credentials.ReadServerAsync();
        if (server is null)
        {
            return null;
        }

        using HttpRequestMessage request = new(HttpMethod.Get, $"{server.Value.Url}/rider/state");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.Value.Key);

        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync(cancellationToken)
                : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>Check the worker is reachable and the key is accepted.</summary>
    public async Task<bool> CheckAsync(string url, string key, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, $"{url.TrimEnd('/')}/status");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }
}
