// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace WhenWillTheBus.Server.Devices;

/// <summary>
/// One phone's Live Activity, waiting to be updated.
/// </summary>
/// <param name="PushToken">
/// The token for THIS ACTIVITY, not for the device. The app gets it from
/// <c>Activity.pushTokenUpdates</c> after starting the activity locally, and it
/// changes — the app must re-register whenever it does.
/// </param>
public sealed record RegisteredActivity(
    string JourneyId,
    long ChildId,
    string PushToken,
    DateTimeOffset RegisteredAt);

/// <summary>
/// Remembers which Live Activities are running, so the worker knows where to
/// send an update.
/// </summary>
/// <remarks>
/// Keyed by push token, because one journey can legitimately be open on several
/// phones — both parents watching the same bus — and each has its own activity.
/// </remarks>
public sealed class DeviceRegistry(ILogger<DeviceRegistry> logger)
{
    private readonly ConcurrentDictionary<string, RegisteredActivity> _activities = [];
    private string? _path;

    public IReadOnlyCollection<RegisteredActivity> All => _activities.Values.ToList();

    public void Register(RegisteredActivity activity)
    {
        _activities[activity.PushToken] = activity;
        logger.LogInformation(
            "Live Activity registered for journey {JourneyId} (child {ChildId})",
            activity.JourneyId,
            activity.ChildId);
    }

    public IReadOnlyList<RegisteredActivity> ForJourney(string journeyId) =>
        _activities.Values.Where(activity => activity.JourneyId == journeyId).ToList();

    /// <summary>
    /// Forget a token Apple has told us is dead. Continuing to push to it wastes
    /// the budget and buries real failures in the noise.
    /// </summary>
    public void Forget(string pushToken)
    {
        if (_activities.TryRemove(pushToken, out RegisteredActivity? gone))
        {
            logger.LogInformation("Forgot a dead activity token for journey {JourneyId}", gone.JourneyId);
        }
    }

    /// <summary>
    /// Drop registrations older than iOS would keep the activity for anyway.
    /// </summary>
    /// <remarks>
    /// iOS retires an activity roughly eight hours after its last update, which
    /// is why a morning card is gone before the afternoon bus leaves school.
    /// Anything older than that is certainly not on screen.
    /// </remarks>
    public int Prune(DateTimeOffset now)
    {
        int removed = 0;
        foreach (RegisteredActivity activity in _activities.Values)
        {
            if (now - activity.RegisteredAt > TimeSpan.FromHours(8)
                && _activities.TryRemove(activity.PushToken, out _))
            {
                removed++;
            }
        }

        return removed;
    }

    /// <summary>Survive a restart: a worker that forgets its tokens stops updating mid-journey.</summary>
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
                string? pushToken = item.GetProperty("pushToken").GetString();
                string? journeyId = item.GetProperty("journeyId").GetString();
                if (pushToken is null || journeyId is null)
                {
                    continue;
                }

                _activities[pushToken] = new RegisteredActivity(
                    journeyId,
                    item.GetProperty("childId").GetInt64(),
                    pushToken,
                    DateTimeOffset.Parse(
                        item.GetProperty("registeredAt").GetString()!,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind));
            }
        }
        catch (Exception error) when (error is JsonException or FormatException or KeyNotFoundException)
        {
            logger.LogWarning(error, "Could not read the activity registry; starting empty");
        }
    }

    public async Task SaveAsync(CancellationToken token = default)
    {
        if (_path is null)
        {
            return;
        }

        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartArray();
            foreach (RegisteredActivity activity in _activities.Values)
            {
                writer.WriteStartObject();
                writer.WriteString("journeyId", activity.JourneyId);
                writer.WriteNumber("childId", activity.ChildId);
                writer.WriteString("pushToken", activity.PushToken);
                writer.WriteString("registeredAt", activity.RegisteredAt.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = _path + ".tmp";
        await File.WriteAllBytesAsync(temporary, stream.ToArray(), token).ConfigureAwait(false);
        File.Move(temporary, _path, overwrite: true);
    }
}
