// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WhenWillTheBus.Core.Api;

namespace WhenWillTheBus.Server.Media;

/// <summary>
/// Searching and adding through Radarr and Sonarr.
/// </summary>
/// <remarks>
/// SEARCH GOES THROUGH THE INSTANCE, not through TMDB directly. It costs an
/// extra hop and is worth it twice over: no second API key to hold, and the
/// results are by construction things the instance will accept — a title found
/// this way can always be added, which is not true of anything found elsewhere.
/// It also answers "do we already have this" for free, because the instance
/// stamps a non-zero id on anything already in its library.
///
/// NOTE ON THE OMBI THAT ALSO RUNS HERE: it is a second request system pointed
/// at the same Radarr and Sonarr, and neither knows about the other's pending
/// requests. Adding the same title through both is the collision that matters,
/// so this refuses anything the instance already holds — which is where a
/// double-add would actually land.
/// </remarks>
public sealed class ArrClient(HttpClient http, IOptions<MediaOptions> options, ILogger<ArrClient> logger)
{
    private readonly MediaOptions _options = options.Value;

    public bool Supports(MediaKind kind) => For(kind).Configured;

    /// <summary>Find things matching a search term.</summary>
    public async Task<IReadOnlyList<MediaResult>> SearchAsync(
        MediaKind kind, string term, CancellationToken token = default)
    {
        ArrOptions arr = For(kind);
        if (!arr.Configured || string.IsNullOrWhiteSpace(term))
        {
            return [];
        }

        string path = kind is MediaKind.Movie ? "movie/lookup" : "series/lookup";
        using JsonDocument? found = await GetAsync(
            arr, $"{path}?term={WebUtility.UrlEncode(term.Trim())}", token).ConfigureAwait(false);

        if (found is null)
        {
            return [];
        }

        List<MediaResult> results = [];
        foreach (JsonElement item in found.RootElement.EnumerateArray())
        {
            long remoteId = item.Long(kind is MediaKind.Movie ? "tmdbId" : "tvdbId") ?? 0;
            string? title = item.String("title");
            if (remoteId == 0 || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            results.Add(new MediaResult(
                kind,
                title,
                (int?)item.Long("year"),
                remoteId,
                item.String("overview"),
                Poster(item),

                // A non-zero id means the instance is already tracking it.
                // Everything a lookup returns that is NOT in the library comes
                // back with id 0, which is why this is a reliable test.
                (item.Long("id") ?? 0) != 0));
        }

        return results;
    }

    /// <summary>Add something, or say why not.</summary>
    public async Task<AddOutcome> AddAsync(
        MediaKind kind, long remoteId, CancellationToken token = default)
    {
        ArrOptions arr = For(kind);
        if (!arr.Configured)
        {
            return new AddOutcome(false, $"{Name(kind)} is not configured on the worker.");
        }

        // FETCHED AGAIN RATHER THAN TRUSTED. The phone sends an identifier and
        // nothing else, so the payload is built from what the instance says
        // about that identifier right now -- a body assembled on a phone would
        // be a body a phone could get wrong, or change.
        string idField = kind is MediaKind.Movie ? "tmdbId" : "tvdbId";
        string path = kind is MediaKind.Movie ? "movie/lookup/tmdb" : "series/lookup";
        string query = kind is MediaKind.Movie
            ? $"{path}?tmdbId={remoteId}"
            : $"{path}?term=tvdb:{remoteId}";

        using JsonDocument? found = await GetAsync(arr, query, token).ConfigureAwait(false);
        if (found is null)
        {
            return new AddOutcome(false, $"{Name(kind)} could not look that up.");
        }

        JsonElement item = found.RootElement.ValueKind == JsonValueKind.Array
            ? found.RootElement.EnumerateArray().FirstOrDefault()
            : found.RootElement;

        if (item.ValueKind != JsonValueKind.Object)
        {
            return new AddOutcome(false, "That could not be found any more.");
        }

        if ((item.Long("id") ?? 0) != 0)
        {
            return new AddOutcome(false, "Already in the library.");
        }

        string? root = arr.RootFolder ?? await FirstAsync(arr, "rootfolder", "path", token).ConfigureAwait(false);
        if (root is null)
        {
            return new AddOutcome(false, $"{Name(kind)} has no root folder configured.");
        }

        int profile = arr.QualityProfileId
            ?? (int?)await FirstIdAsync(arr, "qualityprofile", token).ConfigureAwait(false)
            ?? 1;

        string body = ArrPayload.For(kind, item, idField, remoteId, root, profile);
        (bool ok, string detail) = await PostAsync(
            arr, kind is MediaKind.Movie ? "movie" : "series", body, token).ConfigureAwait(false);

        if (ok)
        {
            logger.LogInformation(
                "Added {Kind} {Title} ({Id}) to the library", kind, item.String("title"), remoteId);
            return new AddOutcome(true, "Added — it will start downloading.");
        }

        return new AddOutcome(false, detail);
    }

    /// <summary>
    /// How much of something has landed, or null when that cannot be told:
    /// not configured, not reachable, or not in the library at all.
    /// </summary>
    /// <remarks>
    /// Asked by the same identifier the request carries, so nothing of the
    /// instance's own ids has to be remembered. A film is ready when it has a
    /// file. A series is "complete" when every episode the instance is meant
    /// to fetch has one -- percentOfEpisodes, Sonarr's own figure, so a series
    /// still airing is complete once it has caught up rather than never.
    /// </remarks>
    public async Task<Readiness?> ReadinessAsync(MediaKind kind, long remoteId, CancellationToken token = default) =>
        (await LookupAsync(kind, remoteId, token).ConfigureAwait(false))?.Entry?.Readiness;

    /// <summary>
    /// What the library holds for a title. Null when the instance could not be
    /// asked; an Entry of null when it was asked and does not have it.
    /// </summary>
    public async Task<(LibraryEntry? Entry, bool Asked)?> LookupAsync(
        MediaKind kind, long remoteId, CancellationToken token = default)
    {
        ArrOptions arr = For(kind);
        if (!arr.Configured)
        {
            return null;
        }

        string query = kind is MediaKind.Movie ? $"movie?tmdbId={remoteId}" : $"series?tvdbId={remoteId}";
        using JsonDocument? found = await GetAsync(arr, query, token).ConfigureAwait(false);
        if (found is null || found.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        JsonElement item = found.RootElement.EnumerateArray().FirstOrDefault();
        return item.ValueKind == JsonValueKind.Object
            ? (new LibraryEntry(ArrPayload.ReadinessOf(kind, item), item.String("titleSlug")), true)
            : (null, true);
    }

    /// <summary>The instance's settings, for building links to it.</summary>
    public ArrOptions OptionsFor(MediaKind kind) => For(kind);

    private ArrOptions For(MediaKind kind) => kind is MediaKind.Movie ? _options.Radarr : _options.Sonarr;

    private static string Name(MediaKind kind) => kind is MediaKind.Movie ? "Radarr" : "Sonarr";

    private static string? Poster(JsonElement item)
    {
        foreach (JsonElement image in item.Array("images"))
        {
            if (image.String("coverType") == "poster")
            {
                return image.String("remoteUrl") ?? image.String("url");
            }
        }

        return null;
    }

    private async Task<JsonDocument?> GetAsync(ArrOptions arr, string path, CancellationToken token)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, Address(arr, path));
            request.Headers.Add("X-Api-Key", arr.ApiKey);

            using HttpResponseMessage response = await http.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("{Url} answered {Status}", path, (int)response.StatusCode);
                return null;
            }

            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(error, "Could not reach {Url}", path);
            return null;
        }
    }

    private async Task<(bool Ok, string Detail)> PostAsync(
        ArrOptions arr, string path, string body, CancellationToken token)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, Address(arr, path))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-Api-Key", arr.ApiKey);

            using HttpResponseMessage response = await http.SendAsync(request, token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return (true, "Added");
            }

            // The instances explain themselves well when they refuse, and the
            // explanation is usually the actionable part -- "no root folder",
            // "already exists". Passing it through beats inventing a summary.
            string said = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            logger.LogWarning("Add refused with {Status}: {Body}", (int)response.StatusCode, Clip(said, 300));
            return (false, ArrPayload.Explain(said, (int)response.StatusCode));
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(error, "Could not reach {Url}", path);
            return (false, "The media server could not be reached.");
        }
    }

    private async Task<string?> FirstAsync(ArrOptions arr, string path, string field, CancellationToken token)
    {
        using JsonDocument? document = await GetAsync(arr, path, token).ConfigureAwait(false);
        return document?.RootElement.EnumerateArray().FirstOrDefault().String(field);
    }

    private async Task<long?> FirstIdAsync(ArrOptions arr, string path, CancellationToken token)
    {
        using JsonDocument? document = await GetAsync(arr, path, token).ConfigureAwait(false);
        return document?.RootElement.EnumerateArray().FirstOrDefault().Long("id");
    }

    private static string Address(ArrOptions arr, string path) =>
        $"{arr.Url.TrimEnd('/')}/api/v3/{path}";

    private static string Clip(string text, int limit) =>
        text.Length <= limit ? text : text[..limit] + "…";
}
