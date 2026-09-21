// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WhenWillTheBus.Core.Notifications;

namespace WhenWillTheBus.Server.Apns;

/// <summary>How urgently a push should arrive, and whether it may interrupt.</summary>
public enum PushUrgency
{
    /// <summary>
    /// A periodic refresh. Goes out at low priority with no alert, so it updates
    /// the card without buzzing a wrist.
    /// </summary>
    Passive,

    /// <summary>
    /// Something actually changed that the reader wants to know now — a stage
    /// transition. ONLY these carry an alert.
    /// </summary>
    TimeSensitive,
}

/// <summary>The outcome of one push, including the reasons worth acting on.</summary>
public sealed record PushResult(bool Delivered, HttpStatusCode Status, string? Reason)
{
    /// <summary>
    /// Whether the device token is dead and should be forgotten. Apple says so
    /// explicitly, and continuing to push to it wastes the budget and hides real
    /// failures in the noise.
    /// </summary>
    public bool TokenIsDead =>
        Status == HttpStatusCode.Gone
        || Reason is "BadDeviceToken" or "Unregistered" or "ExpiredToken";
}

/// <summary>
/// Sends Live Activity updates to APNs over HTTP/2, signed with a JWT.
/// </summary>
/// <remarks>
/// This is the channel that makes the whole rewrite worth doing. A LOCALLY
/// STARTED activity updated by push is a far more reliable path than
/// push-to-start, which fails silently when the app is closed and whose token
/// goes stale on Apple's side with no signal at all.
/// </remarks>
public sealed class ApnsClient(
    HttpClient http,
    IOptions<ApnsOptions> options,
    ILogger<ApnsClient> logger)
{
    /// <summary>
    /// Apple rejects a token older than an hour and rejects regenerating one
    /// more often than every twenty minutes. Fifty minutes sits clear of both.
    /// </summary>
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(50);

    private readonly ApnsOptions _options = options.Value;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private readonly Lock _keyLock = new();

    private ECDsa? _key;
    private string? _token;
    private DateTimeOffset _tokenIssued = DateTimeOffset.MinValue;

    /// <summary>Update a running Live Activity.</summary>
    /// <param name="deviceToken">The activity's push token, not the device's.</param>
    /// <param name="staleAfter">
    /// When iOS should start showing the card as out of date. Set it a little
    /// beyond the next expected push so a missed update is visibly stale rather
    /// than silently wrong.
    /// </param>
    public Task<PushResult> UpdateAsync(
        string deviceToken,
        ILiveActivityState contentState,
        PushUrgency urgency,
        DateTimeOffset? staleAfter = null,
        (string Title, string Body)? alert = null,
        CancellationToken cancellationToken = default) =>
        SendAsync(deviceToken, "update", contentState, urgency, staleAfter, alert, null, cancellationToken);

    /// <summary>End a Live Activity.</summary>
    /// <param name="dismissAt">
    /// When to take the card away. Leaving the FINISHED state on screen for a
    /// few minutes is deliberate: whatever was pushed last is what stands on the
    /// phone, and ending mid-ride once left "riding home, 7 min" frozen there.
    /// </param>
    public Task<PushResult> EndAsync(
        string deviceToken,
        ILiveActivityState contentState,
        DateTimeOffset? dismissAt = null,
        CancellationToken cancellationToken = default) =>
        SendAsync(deviceToken, "end", contentState, PushUrgency.Passive, null, null, dismissAt, cancellationToken);

    private async Task<PushResult> SendAsync(
        string deviceToken,
        string eventName,
        ILiveActivityState contentState,
        PushUrgency urgency,
        DateTimeOffset? staleAfter,
        (string Title, string Body)? alert,
        DateTimeOffset? dismissAt,
        CancellationToken cancellationToken)
    {
        string payload = BuildPayload(eventName, contentState, staleAfter, alert, dismissAt);

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            $"https://{_options.Host}/3/device/{deviceToken}")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        request.Headers.TryAddWithoutValidation(
            "authorization", $"bearer {await AuthenticationTokenAsync(cancellationToken).ConfigureAwait(false)}");
        request.Headers.TryAddWithoutValidation("apns-topic", _options.LiveActivityTopic);
        request.Headers.TryAddWithoutValidation("apns-push-type", "liveactivity");

        // Priority 10 may interrupt; 5 is coalesced by the system and is what a
        // periodic refresh deserves.
        request.Headers.TryAddWithoutValidation(
            "apns-priority", urgency == PushUrgency.TimeSensitive ? "10" : "5");

        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return new PushResult(true, response.StatusCode, null);
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            string? reason = ReadReason(body);

            logger.LogWarning(
                "APNs rejected a {Event} push: {Status} {Reason}", eventName, (int)response.StatusCode, reason);

            return new PushResult(false, response.StatusCode, reason);
        }
        catch (HttpRequestException error)
        {
            logger.LogWarning(error, "Could not reach APNs");
            return new PushResult(false, HttpStatusCode.ServiceUnavailable, error.Message);
        }
    }

    private static string? ReadReason(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("reason", out JsonElement reason)
                ? reason.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildPayload(
        string eventName,
        ILiveActivityState contentState,
        DateTimeOffset? staleAfter,
        (string Title, string Body)? alert,
        DateTimeOffset? dismissAt)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("aps");

            writer.WriteNumber("timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            writer.WriteString("event", eventName);

            if (staleAfter is not null)
            {
                writer.WriteNumber("stale-date", staleAfter.Value.ToUnixTimeSeconds());
            }

            if (dismissAt is not null)
            {
                writer.WriteNumber("dismissal-date", dismissAt.Value.ToUnixTimeSeconds());
            }

            // ONLY a stage change carries an alert. Repeating what the activity
            // already shows is not worth a buzz on the wrist every few minutes —
            // that was a real complaint, not a hypothetical one.
            if (alert is not null)
            {
                writer.WriteStartObject("alert");
                writer.WriteString("title", alert.Value.Title);
                writer.WriteString("body", alert.Value.Body);
                writer.WriteEndObject();
            }

            writer.WritePropertyName("content-state");
            contentState.Write(writer);

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The provider token, regenerated when it is old enough to need it.</summary>
    private async Task<string> AuthenticationTokenAsync(CancellationToken cancellationToken)
    {
        if (_token is not null && DateTimeOffset.UtcNow - _tokenIssued < TokenLifetime)
        {
            return _token;
        }

        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow - _tokenIssued < TokenLifetime)
            {
                return _token;
            }

            DateTimeOffset issued = DateTimeOffset.UtcNow;
            string header = ToBase64Url($$"""{"alg":"ES256","kid":"{{_options.KeyId}}"}""");
            string claims = ToBase64Url($$"""{"iss":"{{_options.TeamId}}","iat":{{issued.ToUnixTimeSeconds()}}}""");
            string signingInput = $"{header}.{claims}";

            byte[] signature;
            lock (_keyLock)
            {
                _key ??= LoadKey();

                // SignData returns IEEE P1363 (r||s), which is exactly what JWS
                // ES256 wants — no DER unwrapping needed.
                signature = _key.SignData(
                    Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }

            _token = $"{signingInput}.{ToBase64Url(signature)}";
            _tokenIssued = issued;
            return _token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private ECDsa LoadKey()
    {
        string pem = _options.PrivateKeyPem
            ?? (_options.PrivateKeyPath is not null && File.Exists(_options.PrivateKeyPath)
                ? File.ReadAllText(_options.PrivateKeyPath)
                : throw new InvalidOperationException(
                    "No APNs signing key. Set Apns:PrivateKeyPath to a mounted .p8, or Apns:PrivateKeyPem as a secret."));

        ECDsa key = ECDsa.Create();
        key.ImportFromPem(pem);
        return key;
    }

    private static string ToBase64Url(string value) => ToBase64Url(Encoding.UTF8.GetBytes(value));

    private static string ToBase64Url(byte[] value) => Base64Url.EncodeToString(value);
}
