// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using WhenWillTheBus.Core.Storage;

namespace WhenWillTheBus.Server.Devices;

/// <summary>
/// One phone that has introduced itself to the worker.
/// </summary>
/// <param name="Id">
/// The app's identifierForVendor: a UUID iOS issues per VENDOR per device, and
/// the only device identifier Apple permits an app to use for this. It is not a
/// hardware identifier — it changes when the last app from this vendor is
/// removed from the phone, which is exactly the behaviour that makes it
/// acceptable, and the reason a re-installed phone appears here as a new row.
/// </param>
/// <param name="Label">
/// What its owner called it. Necessary rather than decorative: UIDevice.name
/// has returned a generic model name since iOS 16 unless the app holds an
/// entitlement Apple grants for fleet management, so "Angela's phone" can only
/// come from somebody typing it.
/// </param>
/// <param name="Model">A MODEL, e.g. "iPhone17,1" — shared by every unit of that product.</param>
public sealed record ClientIdentity(
    string Id,
    string? Label,
    string? Model,
    string? SystemVersion,
    string? AppVersion,
    string? Build,
    bool Sandbox,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    int Visits)
{
    /// <summary>
    /// The DEVICE's push token, for an ordinary notification.
    /// </summary>
    /// <remarks>
    /// Not an activity's token and not interchangeable with one: this comes
    /// from registerForRemoteNotifications, addresses the app rather than a
    /// card, and is sent to the bare bundle-id topic. Null until the phone has
    /// been asked for notification permission and said yes.
    /// </remarks>
    public string? DeviceToken { get; init; }

    /// <summary>
    /// The phone's PUSH-TO-START token, which lets the worker put a Live
    /// Activity on the Lock Screen without the app having been opened.
    /// </summary>
    /// <remarks>
    /// A third kind of token, and like the other two it is not interchangeable
    /// with either: it is issued per APP for one activity TYPE, it is sent to
    /// the Live Activity topic, and it starts a card rather than updating one.
    /// Null on a phone older than iOS 17.2, or on a build from before it was
    /// sent -- and the worker falls back to an ordinary notification then.
    /// </remarks>
    public string? StartToken { get; init; }
}

/// <summary>
/// Remembers which phones are talking to this worker.
/// </summary>
/// <remarks>
/// For the management page, and for one diagnostic question that was otherwise
/// unanswerable: whether a phone that has stopped showing a card is failing to
/// reach the worker, or reaching it and being served something wrong. Those look
/// identical from the sofa and want opposite fixes.
///
/// DELIBERATELY NARROW. This holds what identifies a PHONE to its owner and
/// nothing that identifies a PERSON: no advertising identifier, no serial or
/// UDID, no MAC address, no phone number, no account, and no location — the
/// worker already knows where a child is, and a page that joined the two would
/// be a worse thing to leave logged in than either.
/// </remarks>
public sealed class ClientRegistry(ILogger<ClientRegistry> logger)
{
    /// <summary>
    /// How long a phone is remembered after it stops calling.
    /// </summary>
    /// <remarks>
    /// Long enough to cover a holiday, so a phone that was simply away does not
    /// come back as a stranger; short enough that a replaced handset does not
    /// sit on the page for ever.
    /// </remarks>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(60);

    private readonly ConcurrentDictionary<string, ClientIdentity> _clients = [];
    private string? _path;

    public IReadOnlyList<ClientIdentity> All =>
        _clients.Values.OrderByDescending(client => client.LastSeen).ToList();

    public ClientIdentity? Find(string? id) =>
        id is not null && _clients.TryGetValue(id, out ClientIdentity? found) ? found : null;

    /// <summary>Record a phone saying hello, merging with whatever is already known.</summary>
    public ClientIdentity Greet(ClientIdentity arriving, DateTimeOffset now)
    {
        return _clients.AddOrUpdate(
            arriving.Id,
            arriving with { FirstSeen = now, LastSeen = now, Visits = 1 },
            (_, existing) =>
            {
                bool renamed = arriving.Label is not null && arriving.Label != existing.Label;
                if (renamed)
                {
                    logger.LogInformation(
                        "Client {Id} is now called {Label}", Short(arriving.Id), arriving.Label);
                }

                // FirstSeen belongs to the registry, not to the caller: a phone
                // reporting its own first contact could quietly rewrite when it
                // arrived. Everything else is the phone's to update, because
                // it is the only thing that knows its own iOS or app version.
                return existing with
                {
                    // Null means "this phone did not say", not "it has none" --
                    // an older build, or permission not yet granted. Blanking a
                    // token on a partial hello would silence a phone that is
                    // perfectly reachable.
                    DeviceToken = arriving.DeviceToken ?? existing.DeviceToken,
                    StartToken = arriving.StartToken ?? existing.StartToken,
                    Label = arriving.Label ?? existing.Label,
                    Model = arriving.Model ?? existing.Model,
                    SystemVersion = arriving.SystemVersion ?? existing.SystemVersion,
                    AppVersion = arriving.AppVersion ?? existing.AppVersion,
                    Build = arriving.Build ?? existing.Build,
                    Sandbox = arriving.Sandbox,
                    LastSeen = now,
                    Visits = existing.Visits + 1,
                };
            });
    }

    /// <summary>
    /// Stop using a push-to-start token Apple has said is dead.
    /// </summary>
    /// <remarks>
    /// Only the start token: the phone is still there, and still reachable by
    /// an ordinary notification. It sends a fresh one the next time it opens.
    /// </remarks>
    public bool ForgetStartToken(string id)
    {
        if (!_clients.TryGetValue(id, out ClientIdentity? client) || client.StartToken is null)
        {
            return false;
        }

        _clients[id] = client with { StartToken = null };
        logger.LogInformation("Forgot a dead push-to-start token for client {Id}", Short(id));
        return true;
    }

    public int Prune(DateTimeOffset now)
    {
        int removed = 0;
        foreach (ClientIdentity client in _clients.Values)
        {
            if (now - client.LastSeen > Retention && _clients.TryRemove(client.Id, out _))
            {
                removed++;
            }
        }

        return removed;
    }

    /// <summary>Identify a client in a log line without writing the whole identifier into it.</summary>
    public static string Short(string id) => id.Length <= 8 ? id : id[..8];

    public async Task LoadAsync(string path, CancellationToken token = default)
    {
        _path = path;
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                await File.ReadAllTextAsync(path, token).ConfigureAwait(false));

            foreach (JsonElement item in document.RootElement.EnumerateArray())
            {
                string? id = Text(item, "id");
                if (id is null)
                {
                    continue;
                }

                _clients[id] = new ClientIdentity(
                    id,
                    Text(item, "label"),
                    Text(item, "model"),
                    Text(item, "systemVersion"),
                    Text(item, "appVersion"),
                    Text(item, "build"),
                    !item.TryGetProperty("sandbox", out JsonElement flag) || flag.GetBoolean(),
                    Instant(item, "firstSeen"),
                    Instant(item, "lastSeen"),
                    item.TryGetProperty("visits", out JsonElement visits) ? visits.GetInt32() : 1)
                {
                    DeviceToken = Text(item, "deviceToken"),
                    StartToken = Text(item, "startToken"),
                };
            }
        }
        catch (Exception error) when (error is JsonException or FormatException or KeyNotFoundException)
        {
            logger.LogWarning(error, "Could not read the client registry; starting empty");
        }
    }

    /// <summary>One save at a time.</summary>
    /// <remarks>
    /// Phones call in concurrently -- one sent two hellos in the same second on
    /// 7 Oct -- and two saves overlapping can each snapshot the registry and
    /// then land in either order, putting the OLDER one on disk last.
    /// </remarks>
    private readonly SemaphoreSlim _saving = new(1, 1);

    public async Task SaveAsync(CancellationToken token = default)
    {
        await _saving.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await WriteAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _saving.Release();
        }
    }

    private async Task WriteAsync(CancellationToken token)
    {
        if (_path is null)
        {
            return;
        }

        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartArray();
            foreach (ClientIdentity client in _clients.Values)
            {
                writer.WriteStartObject();
                writer.WriteString("id", client.Id);
                Write(writer, "label", client.Label);
                Write(writer, "model", client.Model);
                Write(writer, "systemVersion", client.SystemVersion);
                Write(writer, "appVersion", client.AppVersion);
                Write(writer, "build", client.Build);
                writer.WriteBoolean("sandbox", client.Sandbox);
                writer.WriteString("firstSeen", client.FirstSeen.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteString("lastSeen", client.LastSeen.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteNumber("visits", client.Visits);
                Write(writer, "deviceToken", client.DeviceToken);
                Write(writer, "startToken", client.StartToken);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        await AtomicFile.WriteAsync(_path, stream.ToArray(), token).ConfigureAwait(false);
    }

    private static void Write(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset Instant(JsonElement element, string name) =>
        Text(element, name) is string text
            ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            : DateTimeOffset.UnixEpoch;
}
