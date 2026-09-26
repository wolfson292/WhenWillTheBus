// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using WhenWillTheBus.Core.Api;

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
    private readonly ConcurrentQueue<MediaRequest> _requests = [];
    private string? _path;
    private int _limit = 200;

    public IReadOnlyList<MediaRequest> Recent(int count = 50) =>
        _requests.Reverse().Take(count).ToList();

    public void Record(MediaRequest request)
    {
        _requests.Enqueue(request);
        while (_requests.Count > _limit && _requests.TryDequeue(out _))
        {
            // Bounded by count. A household asks for a few things a week, so
            // this is years of history in a file measured in kilobytes.
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

                _requests.Enqueue(new MediaRequest(
                    item.String("kind") == "series" ? MediaKind.Series : MediaKind.Movie,
                    title,
                    (int?)item.Long("year"),
                    item.Long("remoteId") ?? 0,
                    item.String("posterUrl"),
                    item.String("requestedBy"),
                    DateTimeOffset.Parse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    item.String("outcome") ?? "unknown"));
            }
        }
        catch (Exception error) when (error is JsonException or FormatException)
        {
            logger.LogWarning(error, "Could not read the request log; starting empty");
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
            foreach (MediaRequest request in _requests)
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
