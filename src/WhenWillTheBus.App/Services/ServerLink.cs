// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net.Http.Headers;
using System.Text;
using WhenWillTheBus.Core.Api;

namespace WhenWillTheBus.App.Services;

/// <summary>What the worker says this phone may do.</summary>
public sealed record WorkerRole(bool IsAdmin, bool UpdateAvailable, string? LatestBuild);

/// <summary>One thing MagicMovieNight suggests putting on.</summary>
public sealed record TonightPick(
    int Rank,
    string Title,
    int? Year,
    string Kind,
    string? Pitch,
    string? WhereToWatch,
    string? PosterUrl,
    bool InLibrary);

/// <summary>Another phone in the household, as an admin sees it.</summary>
public sealed record FamilyPhone(
    string Id,
    string? Label,
    string? Model,
    string? Build,
    bool UpdateAvailable,
    bool Reachable,
    bool Sandbox);

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

        // Which APNs environment this build's push token belongs to. A
        // sandbox token is rejected outright by production and vice versa,
        // always as a silent non-delivery -- and one worker has to serve a
        // TestFlight family alongside a development phone, so it must be told
        // rather than left to guess.
#if APNS_PRODUCTION
        const string Environment = "production";
#else
        const string Environment = "sandbox";
#endif

        // WHICH PHONE THIS IS. ActivityKit reissues an activity's push token
        // while the card is running, and every reissue arrives here as another
        // registration. Keyed only by token the worker cannot tell one phone's
        // rotated token from a second parent watching the same bus, so it keeps
        // both and pushes twice to one card -- and a retired token often still
        // answers 200, so nothing ever cleans it up.
        //
        // identifierForVendor is stable for this journey's lifetime, which is
        // all this needs. It is already sent in the introduction; null only on
        // a device that refuses it, and the worker falls back to the old
        // token-only behaviour then.
        string device = DeviceIdentity.VendorId is string id
            ? $@",""deviceId"":""{id}"""
            : string.Empty;

        string body = $$"""
            {"journeyId":"{{journeyId}}","childId":{{childId}},"pushToken":"{{pushToken}}","environment":"{{Environment}}"{{device}}}
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

    /// <summary>
    /// Tell the worker which phone this is. Safe to call repeatedly.
    /// </summary>
    /// <remarks>
    /// Sent SEPARATELY from the activity registration rather than folded into
    /// it, because the two answer different questions. A phone with no card
    /// running is exactly the phone somebody is trying to diagnose, and one
    /// that only announced itself alongside an activity would be invisible for
    /// the whole of the day it was failing.
    /// </remarks>
    public async Task<bool> HelloAsync(CancellationToken cancellationToken = default)
    {
        (string Url, string Key)? server = await credentials.ReadServerAsync();
        if (server is null || DeviceIdentity.HelloJson() is not string body)
        {
            return false;
        }

        using HttpRequestMessage request = new(HttpMethod.Post, $"{server.Value.Url}/clients/hello")
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
            return false;
        }
    }

    /// <summary>What this phone is allowed to do, and whether it is out of date.</summary>
    /// <remarks>
    /// ASKED OF THE WORKER, not worked out here. A phone cannot tell an admin
    /// key from an ordinary one by looking at it, and should not try: the
    /// answer is whatever the worker will actually honour.
    /// </remarks>
    public async Task<WorkerRole> RoleAsync(CancellationToken cancellationToken = default)
    {
        string query = DeviceIdentity.VendorId is string id
            ? $"?clientId={System.Net.WebUtility.UrlEncode(id)}"
            : string.Empty;

        string? body = await ReadAsync($"/me{query}", cancellationToken);
        if (body is null)
        {
            return new WorkerRole(false, false, null);
        }

        try
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(body);
            return new WorkerRole(
                document.RootElement.Bool("admin"),
                document.RootElement.Bool("updateAvailable"),
                document.RootElement.String("latestBuild"));
        }
        catch (System.Text.Json.JsonException)
        {
            return new WorkerRole(false, false, null);
        }
    }

    /// <summary>
    /// The key a setup link should carry.
    /// </summary>
    /// <remarks>
    /// The stored key for an ordinary phone, and the FAMILY key when this one
    /// is an admin -- because sharing the admin key would quietly make the
    /// recipient an admin, and neither phone would show any sign of it.
    /// </remarks>
    public async Task<string?> ShareableKeyAsync(CancellationToken cancellationToken = default)
    {
        (string Url, string Key)? server = await credentials.ReadServerAsync();
        if (server is null)
        {
            return null;
        }

        string? body = await ReadAsync("/admin/family-key", cancellationToken);
        if (body is null)
        {
            // Not an admin, or the worker is unreachable. Either way the key
            // this phone holds is the one to share.
            return server.Value.Key;
        }

        try
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(body);
            return document.RootElement.String("key") ?? server.Value.Key;
        }
        catch (System.Text.Json.JsonException)
        {
            return server.Value.Key;
        }
    }

    /// <summary>The other phones, for an admin screen. Empty unless this key is an admin one.</summary>
    public async Task<IReadOnlyList<FamilyPhone>> PhonesAsync(CancellationToken cancellationToken = default)
    {
        string? body = await ReadAsync("/admin/phones", cancellationToken);
        if (body is null)
        {
            return [];
        }

        try
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(body);
            List<FamilyPhone> phones = [];
            foreach (System.Text.Json.JsonElement item in document.RootElement.Array("phones"))
            {
                phones.Add(new FamilyPhone(
                    item.String("id") ?? string.Empty,
                    item.String("label"),
                    item.String("model"),
                    item.String("build"),
                    item.Bool("updateAvailable"),
                    item.Bool("reachable"),
                    item.Bool("sandbox")));
            }

            return phones;
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>What to watch tonight, from MagicMovieNight. Empty when it has nothing to say.</summary>
    public async Task<IReadOnlyList<TonightPick>> TonightAsync(CancellationToken cancellationToken = default)
    {
        string? body = await ReadAsync("/admin/tonight", cancellationToken);
        if (body is null)
        {
            return [];
        }

        try
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(body);
            List<TonightPick> picks = [];
            foreach (System.Text.Json.JsonElement pick in document.RootElement.Array("picks"))
            {
                picks.Add(new TonightPick(
                    (int)(pick.Long("rank") ?? 0),
                    pick.String("title") ?? "(untitled)",
                    (int?)pick.Long("year"),
                    pick.String("kind") ?? "Movie",
                    pick.String("pitch"),
                    pick.String("whereToWatch"),
                    pick.String("posterUrl"),
                    pick.Bool("inLibrary")));
            }

            return picks;
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>Send somebody a notification. Returns what to show the sender.</summary>
    public async Task<string> NudgeAsync(
        string clientId, string title, string body, string? openUrl = null,
        CancellationToken cancellationToken = default)
    {
        (string Url, string Key)? server = await credentials.ReadServerAsync();
        if (server is null)
        {
            return "No worker is configured.";
        }

        string payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            clientId,
            title,
            body,
            openUrl,
        });

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, $"{server.Value.Url}/admin/notify")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.Value.Key);

            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
            string said = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                return "This phone is not an admin.";
            }

            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(said);
            if (document.RootElement.Bool("sent"))
            {
                return "Sent.";
            }

            return document.RootElement.String("error") ?? "It was not delivered.";
        }
        catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException)
        {
            return "Could not reach the worker.";
        }
    }

    private async Task<string?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        (string Url, string Key)? server = await credentials.ReadServerAsync();
        if (server is null)
        {
            return null;
        }

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, $"{server.Value.Url}{path}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.Value.Key);

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
