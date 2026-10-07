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
    ApnsTokenProvider tokens,
    IOptions<ApnsOptions> options,
    ILogger<ApnsClient> logger)
{
    private readonly ApnsOptions _options = options.Value;

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
        bool? sandbox = null,
        CancellationToken cancellationToken = default,
        bool sound = false) =>
        SendAsync(
            deviceToken, "update", contentState, urgency, staleAfter, alert, null, sandbox, cancellationToken, sound);

    /// <summary>End a Live Activity.</summary>
    /// <param name="dismissAt">
    /// When to take the card away. Usually now: the stage machine has already
    /// held the FINISHED state on screen for its dwell before the journey goes
    /// idle, so the lingering has happened by the time this is called.
    /// </param>
    /// <remarks>
    /// Sent at PRIORITY 10, with no alert.
    /// 
    /// Priority 5 means "deliver when convenient", and on a locked idle phone
    /// iOS will happily sit on it -- observed here as an end push that APNs
    /// accepted with a 200 and that never took effect, leaving a finished card
    /// on the Lock Screen. Ending is a state change the reader should see, so it
    /// goes out promptly; attaching no alert is what keeps it from interrupting.
    /// </remarks>
    public Task<PushResult> EndAsync(
        string deviceToken,
        ILiveActivityState contentState,
        DateTimeOffset? dismissAt = null,
        bool? sandbox = null,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            deviceToken, "end", contentState, PushUrgency.TimeSensitive, null, null, dismissAt, sandbox,
            cancellationToken);

    /// <summary>Put a Live Activity on a phone's Lock Screen without the app.</summary>
    /// <param name="startToken">
    /// The phone's PUSH-TO-START token: one per app per activity type, from
    /// <c>Activity.pushToStartTokenUpdates</c>. Not an activity's token and not
    /// the device's.
    /// </param>
    /// <remarks>
    /// THE CARD THIS STARTS CANNOT BE UPDATED UNTIL THE PHONE SAYS SO. iOS gives
    /// the new activity its own update token and wakes the app in the
    /// background to hand it over; the app then registers it like any other,
    /// and from there the publisher keeps it moving. Until that lands this
    /// card shows what it was started with -- which is why it is only sent at
    /// the moment the bus sets off, when what it starts with is the best
    /// estimate of the morning so far rather than the worst.
    ///
    /// An alert is REQUIRED on a start, and sound is asked for explicitly:
    /// this is the push that replaces somebody opening the app.
    /// </remarks>
    public Task<PushResult> StartAsync(
        string startToken,
        BusActivityIdentity identity,
        ILiveActivityState contentState,
        (string Title, string Body) alert,
        DateTimeOffset? staleAfter = null,
        bool? sandbox = null,
        CancellationToken cancellationToken = default) =>
        PostAsync(
            startToken,
            BuildPayload("start", contentState, staleAfter, alert, null, identity, sound: true),
            _options.LiveActivityTopic,
            "liveactivity",
            "10",
            sandbox,
            "start",
            cancellationToken);

    /// <summary>
    /// An ordinary notification: a title, a body, and a banner on the phone.
    /// </summary>
    /// <remarks>
    /// A DIFFERENT KIND OF PUSH FROM EVERYTHING ELSE HERE, in three ways that
    /// each fail silently if got wrong. The topic is the bare bundle id rather
    /// than the .push-type.liveactivity one; the push type is "alert"; and the
    /// token is the DEVICE's, from registerForRemoteNotifications, not an
    /// activity's. Apple accepts a mismatched topic with a 400 that names the
    /// topic, which is the one merciful part.
    /// </remarks>
    public Task<PushResult> AlertAsync(
        string deviceToken,
        string title,
        string body,
        string? openUrl = null,
        bool? sandbox = null,
        CancellationToken cancellationToken = default)
    {
        StringBuilder payload = new();
        payload.Append("{\"aps\":{\"alert\":{\"title\":")
            .Append(JsonSerializer.Serialize(title))
            .Append(",\"body\":")
            .Append(JsonSerializer.Serialize(body))
            .Append("},\"sound\":\"default\"}");

        // Carried beside the alert rather than inside it: the app reads this
        // when somebody taps, and iOS ignores anything it does not recognise.
        if (!string.IsNullOrWhiteSpace(openUrl))
        {
            payload.Append(",\"openUrl\":").Append(JsonSerializer.Serialize(openUrl));
        }

        payload.Append('}');

        return PostAsync(
            deviceToken, payload.ToString(), _options.BundleId, "alert", "10", sandbox, "alert",
            cancellationToken);
    }

    private Task<PushResult> SendAsync(
        string deviceToken,
        string eventName,
        ILiveActivityState contentState,
        PushUrgency urgency,
        DateTimeOffset? staleAfter,
        (string Title, string Body)? alert,
        DateTimeOffset? dismissAt,
        bool? sandbox,
        CancellationToken cancellationToken,
        bool sound = false) =>
        PostAsync(
            deviceToken,
            BuildPayload(eventName, contentState, staleAfter, alert, dismissAt, sound: sound),
            _options.LiveActivityTopic,
            "liveactivity",

            // Priority 10 may interrupt; 5 is coalesced by the system and is
            // what a periodic refresh deserves.
            urgency == PushUrgency.TimeSensitive ? "10" : "5",
            sandbox,
            eventName,
            cancellationToken);

    private async Task<PushResult> PostAsync(
        string deviceToken,
        string payload,
        string topic,
        string pushType,
        string priority,
        bool? sandbox,
        string what,
        CancellationToken cancellationToken)
    {
        string eventName = what;
        string host = sandbox is null ? _options.Host : ApnsOptions.HostFor(sandbox.Value);

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            $"https://{host}/3/device/{deviceToken}")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        request.Headers.TryAddWithoutValidation(
            "authorization", $"bearer {await tokens.TokenAsync(cancellationToken).ConfigureAwait(false)}");
        request.Headers.TryAddWithoutValidation("apns-topic", topic);
        request.Headers.TryAddWithoutValidation("apns-push-type", pushType);
        request.Headers.TryAddWithoutValidation("apns-priority", priority);

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
                "APNs ({Host}) rejected a {Event} push: {Status} {Reason}",
                host, eventName, (int)response.StatusCode, reason);

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

    /// <summary>The Live Activity payload. Public so its shape can be pinned by a test.</summary>
    public static string BuildPayload(
        string eventName,
        ILiveActivityState contentState,
        DateTimeOffset? staleAfter,
        (string Title, string Body)? alert,
        DateTimeOffset? dismissAt,
        BusActivityIdentity? identity = null,
        bool sound = false)
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
                if (sound)
                {
                    writer.WriteString("sound", "default");
                }

                writer.WriteEndObject();
            }

            // Only a start carries the identity. An update addresses a card
            // that already has one, and iOS ignores these keys on it.
            if (identity is not null)
            {
                writer.WriteString("attributes-type", BusActivityIdentity.TypeName);
                writer.WritePropertyName("attributes");
                identity.Write(writer);
            }

            writer.WritePropertyName("content-state");
            contentState.Write(writer);

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
