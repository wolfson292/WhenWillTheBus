// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.Json;
using WhenWillTheBus.Core.Api;
using WhenWillTheBus.Core.Storage;

namespace WhenWillTheBus.Server.Media;

/// <summary>
/// What the household has asked for.
/// </summary>
/// <remarks>
/// Kept here rather than read back from Radarr and Sonarr, because they record
/// what was ADDED and this records what was ASKED FOR — including the refusals,
/// which are the interesting half. "I asked for that and nothing happened" is
/// the complaint this exists to answer, and the instances keep no trace of it.
///
/// It also keeps WHO asked, which is the difference between a download queue
/// and a family feature.
/// </remarks>
public sealed class RequestLog(ILogger<RequestLog> logger)
{
    /// <summary>
    /// How long a request is watched for its download before giving up.
    /// </summary>
    /// <remarks>
    /// Long enough for something unreleased to come out, or a slow season to
    /// finish; short enough that a request that will never be satisfied stops
    /// being asked about.
    /// </remarks>
    public static readonly TimeSpan WatchFor = TimeSpan.FromDays(30);

    private readonly List<MediaRequest> _requests = [];
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _saving = new(1, 1);
    private string? _path;
    private int _limit = 200;

    public IReadOnlyList<MediaRequest> Recent(int count = 50)
    {
        lock (_gate)
        {
            return _requests.AsEnumerable().Reverse().Take(count).ToList();
        }
    }

    /// <summary>
    /// Requests still waiting to be announced as ready.
    /// </summary>
    /// <remarks>
    /// Only those with somebody to tell. Requests from before phones said who
    /// they were have no recipient, and checking a library for them would be
    /// work with no one at the end of it.
    /// </remarks>
    public IReadOnlyList<MediaRequest> Waiting(DateTimeOffset now)
    {
        lock (_gate)
        {
            return _requests
                .Where(request => request.ReadyAt is null
                    && request.RecipientId is not null
                    && now - request.RequestedAt <= WatchFor)
                .ToList();
        }
    }

    /// <summary>Replace a request with what has since become of it.</summary>
    public void Update(MediaRequest was, MediaRequest now)
    {
        lock (_gate)
        {
            int index = _requests.IndexOf(was);
            if (index >= 0)
            {
                _requests[index] = now;
            }
        }
    }

    public void Record(MediaRequest request)
    {
        lock (_gate)
        {
            _requests.Add(request);

            // Bounded by count. A household asks for a few things a week, so
            // this is years of history in a file measured in kilobytes.
            if (_requests.Count > _limit)
            {
                _requests.RemoveRange(0, _requests.Count - _limit);
            }
        }

        logger.LogInformation(
            "{Who} asked for {Title} ({Year}) — {Outcome}",
            request.RequestedBy ?? "somebody",
            request.Title,
            request.Year,
            request.Outcome);
    }

    public async Task LoadAsync(string path, int limit, CancellationToken token = default)
    {
        _path = path;
        _limit = Math.Max(limit, 1);

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
                string? title = item.String("title");
                string? at = item.String("requestedAt");
                if (title is null || at is null)
                {
                    continue;
                }

                _requests.Add(new MediaRequest(
                    item.String("kind") == "series" ? MediaKind.Series : MediaKind.Movie,
                    title,
                    (int?)item.Long("year"),
                    item.Long("remoteId") ?? 0,
                    item.String("posterUrl"),
                    item.String("requestedBy"),
                    Instant(at)!.Value,
                    item.String("outcome") ?? "unknown")
                {
                    RequestedById = item.String("requestedById"),
                    ForId = item.String("forId"),
                    RequestedFor = item.String("requestedFor"),
                    StartedAt = Instant(item.String("startedAt")),
                    ReadyAt = Instant(item.String("readyAt")),
                });
            }
        }
        catch (Exception error) when (error is JsonException or FormatException)
        {
            logger.LogWarning(error, "Could not read the request log; starting empty");
        }
    }

    /// <summary>One save at a time, so an older snapshot can never land after a newer one.</summary>
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

        List<MediaRequest> requests;
        lock (_gate)
        {
            requests = [.. _requests];
        }

        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartArray();
            foreach (MediaRequest request in requests)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", request.Kind is MediaKind.Series ? "series" : "movie");
                writer.WriteString("title", request.Title);
                if (request.Year is int year)
                {
                    writer.WriteNumber("year", year);
                }

                writer.WriteNumber("remoteId", request.RemoteId);
                if (request.PosterUrl is not null)
                {
                    writer.WriteString("posterUrl", request.PosterUrl);
                }

                if (request.RequestedBy is not null)
                {
                    writer.WriteString("requestedBy", request.RequestedBy);
                }

                writer.WriteString("requestedAt", request.RequestedAt.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteString("outcome", request.Outcome);
                Optional(writer, "requestedById", request.RequestedById);
                Optional(writer, "forId", request.ForId);
                Optional(writer, "requestedFor", request.RequestedFor);
                Optional(writer, "startedAt", request.StartedAt?.ToString("O", CultureInfo.InvariantCulture));
                Optional(writer, "readyAt", request.ReadyAt?.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        await AtomicFile.WriteAsync(_path, stream.ToArray(), token).ConfigureAwait(false);
    }

    private static void Optional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }

    private static DateTimeOffset? Instant(string? text) =>
        text is null ? null : DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
