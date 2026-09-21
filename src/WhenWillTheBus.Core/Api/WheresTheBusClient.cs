// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Text;
using System.Text.Json;
using WhenWillTheBus.Core.Model;

namespace WhenWillTheBus.Core.Api;

/// <summary>
/// Client for the WheresTheBus parent app API.
/// </summary>
/// <remarks>
/// Undocumented, reverse-engineered from the Flutter web client. All calls are
/// POST with a JSON body, and responses are JSON but DO NOT ALWAYS SET A JSON
/// CONTENT TYPE — so decoding is forced rather than negotiated.
///
/// Be a good citizen: this is somebody else's service, running for the benefit
/// of schoolchildren. The API advertises a 15-second refresh and this polls at
/// 30.
/// </remarks>
public sealed class WheresTheBusClient(HttpClient http, WheresTheBusCredentials credentials)
{
    private const string ApiRoot = "https://mdt.wheresthebus.com/";
    private const string ApiPath = "wtbparentapp/api/v2/";

    /// <summary>
    /// Values the Flutter web client sends. The server REJECTS logins that do
    /// not look like a known client, so these go verbatim. If you change them to
    /// something iOS-shaped, test that login still works before relying on it.
    /// </summary>
    private const string AppVersion = "5.2.2";
    private const string DeviceType = "FlutterWeb";
    private const string DeviceOs = "Web_safari_Flutter";

    /// <summary>The login 307-redirects to a per-account shard; one hop is expected.</summary>
    private const int MaxRedirects = 5;

    private readonly SemaphoreSlim _loginLock = new(1, 1);

    /// <summary>The current session, or null before the first login.</summary>
    public string? SessionId { get; private set; }

    /// <summary>The account's shard. Every authenticated call goes here.</summary>
    public string BasePath { get; private set; } = ApiRoot;

    public string? ShardId { get; private set; }

    public string? FirstName { get; private set; }

    public string? LastName { get; private set; }

    /// <summary>Authenticate, and remember the session id and shard base path.</summary>
    public async Task<LoginResult> LoginAsync(CancellationToken cancellationToken = default)
    {
        await _loginLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string body = JsonBody(
                ("emailId", credentials.Email),
                ("password", credentials.Password),
                ("imeiNo", credentials.DeviceId),
                ("deviceType", DeviceType),
                ("sso", 0),
                ("deviceOS", DeviceOs),
                ("versionInstalled", AppVersion));

            using JsonDocument response = await PostAsync($"{ApiRoot}{ApiPath}login", body, cancellationToken)
                .ConfigureAwait(false);
            JsonElement root = response.RootElement;

            if (root.Long("resCode") != 0)
            {
                throw new WheresTheBusAuthException(root.String("mesgStr") ?? "Invalid email or password");
            }

            JsonElement payload = root.Property("payload") ?? default;
            string? sessionId = payload.String("sessionId");
            if (string.IsNullOrEmpty(sessionId))
            {
                throw new WheresTheBusAuthException("Login succeeded but returned no session");
            }

            string basePath = payload.String("basePath") ?? ApiRoot;
            if (!basePath.EndsWith('/'))
            {
                basePath += "/";
            }

            SessionId = sessionId;
            BasePath = basePath;
            ShardId = payload.String("shardId");
            FirstName = payload.String("firstName");
            LastName = payload.String("lastName");

            return new LoginResult(sessionId, basePath, ShardId, FirstName, LastName);
        }
        finally
        {
            _loginLock.Release();
        }
    }

    /// <summary>Account settings plus the list of the account's child buses.</summary>
    public async Task<JsonDocument> GetUserInfoAsync(CancellationToken cancellationToken = default) =>
        await CallAsync(
            "getUserInfo",
            [
                ("imeiNo", credentials.DeviceId),
                ("versionInstalled", AppVersion),

                // An EMPTY tokenId registers no push target, so the family's real
                // phones keep their own notifications. A non-empty one would
                // register THIS client and steal them. Revisiting that is
                // reasonable for a native app that wants the vendor's pushes —
                // but do it deliberately, knowing it changes behaviour for every
                // device on the account.
                ("tokenId", ""),
                ("deviceNotif", true),
            ],
            cancellationToken).ConfigureAwait(false);

    /// <summary>The roster, with AM/PM stop details for each rider.</summary>
    public async Task<JsonDocument> GetAllRidersAsync(CancellationToken cancellationToken = default) =>
        await CallAsync("getAllRiders", [], cancellationToken).ConfigureAwait(false);

    /// <summary>Live bus position and stop distance for one child.</summary>
    /// <param name="lastServerTime">
    /// The previous response's <c>serverTime</c>, to receive only new breadcrumbs.
    /// </param>
    public async Task<RiderInfo> GetRiderInfoAsync(
        string busNumber,
        long childId,
        long lastServerTime = 0,
        CancellationToken cancellationToken = default)
    {
        using JsonDocument response = await CallAsync(
            "getRiderInfoEx",
            [("bid", busNumber), ("chdId", childId), ("lastServerTime", lastServerTime)],
            cancellationToken).ConfigureAwait(false);

        JsonElement root = response.RootElement;
        JsonElement payload = root.Property("payload") ?? default;
        return RiderInfo.From(payload, root.Long("serverTime") ?? 0);
    }

    /// <summary>
    /// Recent badge-scan events. THE ENDPOINT ONLY EVER RETURNS THE CURRENT DAY,
    /// so scans must be accumulated locally and persisted or all history is lost
    /// at midnight.
    /// </summary>
    public async Task<JsonDocument> GetStudentScansAsync(CancellationToken cancellationToken = default) =>
        await CallAsync("getStudentScan", [], cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Call an authenticated endpoint, re-authenticating once if the session has
    /// died.
    /// </summary>
    /// <remarks>
    /// Every response carries a <c>resCode</c>; 0 means success and anything
    /// else is usually a dead session. Re-login ONCE and retry, then give up —
    /// this is what makes long-running polling survive, and the bounded retry is
    /// what stops a genuine outage becoming a hammering loop.
    /// </remarks>
    private async Task<JsonDocument> CallAsync(
        string endpoint,
        (string Key, object? Value)[] arguments,
        CancellationToken cancellationToken)
    {
        if (SessionId is null)
        {
            await LoginAsync(cancellationToken).ConfigureAwait(false);
        }

        string? failure = null;
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            string sessionId = SessionId!;

            // The session goes in BOTH the query string and the body. That is
            // what the web client does, and the server is not reliably happy
            // with only one of them.
            string body = JsonBody([.. arguments, ("sessionId", sessionId)]);
            string url = $"{BasePath}{ApiPath}{endpoint}?sessionId={Uri.EscapeDataString(sessionId)}";

            JsonDocument response = await PostAsync(url, body, cancellationToken).ConfigureAwait(false);
            if (response.RootElement.Long("resCode") == 0)
            {
                return response;
            }

            failure = response.RootElement.String("mesgStr")
                ?? $"resCode {response.RootElement.Long("resCode")}";
            response.Dispose();

            if (attempt == 1)
            {
                await LoginAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        throw new WheresTheBusException($"{endpoint} failed: {failure}");
    }

    /// <summary>
    /// POST a JSON body and decode the reply, following 307/308 redirects by
    /// replaying the body.
    /// </summary>
    /// <remarks>
    /// Redirects are handled here rather than left to <see cref="HttpClient"/>.
    /// The login redirects to a per-account shard and the POST body MUST be
    /// replayed against the target; handlers vary in whether they do that, and
    /// a silent failure here looks exactly like a wrong password.
    /// </remarks>
    private async Task<JsonDocument> PostAsync(string url, string body, CancellationToken cancellationToken)
    {
        string target = url;

        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, target)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException error) when (!cancellationToken.IsCancellationRequested)
            {
                throw new WheresTheBusException($"Timeout talking to {target}", error);
            }
            catch (HttpRequestException error)
            {
                throw new WheresTheBusException($"Error talking to {target}: {error.Message}", error);
            }

            using (response)
            {
                if (response.StatusCode is HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    Uri? location = response.Headers.Location;
                    if (location is null)
                    {
                        throw new WheresTheBusException($"{target} redirected without a location");
                    }

                    target = location.IsAbsoluteUri ? location.ToString() : new Uri(new Uri(target), location).ToString();
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new WheresTheBusAuthException("Session rejected by server");
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new WheresTheBusException($"{target} returned {(int)response.StatusCode}");
                }

                // Forced, not negotiated: the API answers with JSON but does not
                // always say so in the content type.
                string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return JsonDocument.Parse(text);
                }
                catch (JsonException error)
                {
                    throw new WheresTheBusException($"Malformed response from {target}", error);
                }
            }
        }

        throw new WheresTheBusException($"{url} redirected more than {MaxRedirects} times");
    }

    /// <summary>
    /// Build a JSON object without reflection, so this survives the trimming and
    /// AOT compilation an iOS build applies.
    /// </summary>
    private static string JsonBody(params (string Key, object? Value)[] fields)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            foreach ((string key, object? value) in fields)
            {
                switch (value)
                {
                    case null: writer.WriteNull(key); break;
                    case string text: writer.WriteString(key, text); break;
                    case bool flag: writer.WriteBoolean(key, flag); break;
                    case int number: writer.WriteNumber(key, number); break;
                    case long number: writer.WriteNumber(key, number); break;
                    case double number: writer.WriteNumber(key, number); break;
                    default: writer.WriteString(key, value.ToString()); break;
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
