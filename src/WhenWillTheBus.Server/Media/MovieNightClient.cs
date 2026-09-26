// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Microsoft.Extensions.Options;
using WhenWillTheBus.Core.Api;

namespace WhenWillTheBus.Server.Media;

/// <summary>One thing MagicMovieNight suggests watching.</summary>
public sealed record TonightPick(
    int Rank,
    string Title,
    int? Year,
    string Kind,
    string? Pitch,
    string? BasedOn,
    string? WhereToWatch,
    string? PosterUrl,
    bool InLibrary,
    int? Confidence,

    // The identifiers Radarr and Sonarr key on, carried so a pick that is NOT
    // in the library can be asked for without searching for it again by name --
    // which would risk requesting a different film with the same title.
    long? TmdbId,
    long? TvdbId,
    int? RuntimeMinutes);

/// <summary>What was suggested, and when it was worked out.</summary>
public sealed record TonightBoard(
    DateTimeOffset? DecidedAt, IReadOnlyList<TonightPick> Picks, string? Error = null);

/// <summary>
/// Reads tonight's picks from MagicMovieNight.
/// </summary>
/// <remarks>
/// A DIFFERENT APPLICATION, on the same box, that already knows what the
/// household watches across Plex and Trakt and asks Claude what to put on.
/// This does not reimplement any of that; it asks, and shows the answer.
///
/// Everything here fails soft. MagicMovieNight being down, or not yet running
/// a build with the endpoint, must not take out an admin screen whose other
/// two sections work perfectly well -- so an empty board is a normal result
/// rather than an error.
/// </remarks>
public sealed class MovieNightClient(
    HttpClient http, IOptions<MediaOptions> options, ILogger<MovieNightClient> logger)
{
    private readonly MediaOptions _options = options.Value;

    public bool Configured => !string.IsNullOrWhiteSpace(_options.MovieNightUrl);

    /// <summary>
    /// Ask for a fresh suggestion, in words.
    /// </summary>
    /// <remarks>
    /// SLOW AND NOT FREE on the other side: it pools candidates and asks
    /// Claude, which takes tens of seconds and spends tokens. Hence a long
    /// timeout of its own rather than the client's, and hence nothing calls
    /// this on a timer.
    /// </remarks>
    public async Task<TonightBoard> SuggestAsync(
        string? prompt, string? kind, int count = 5, CancellationToken token = default)
    {
        if (!Configured)
        {
            return new TonightBoard(null, [], "MagicMovieNight is not configured on the worker.");
        }

        string body = JsonSerializer.Serialize(new { prompt, kind, count });

        try
        {
            using HttpRequestMessage request = new(
                HttpMethod.Post, $"{_options.MovieNightUrl!.TrimEnd('/')}/api/suggest")
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };

            // Its own budget. The shared client's fifteen seconds is right for
            // reading the last answer and nowhere near enough to make a new one.
            using CancellationTokenSource slower = CancellationTokenSource.CreateLinkedTokenSource(token);
            slower.CancelAfter(TimeSpan.FromMinutes(3));

            using HttpResponseMessage response = await http
                .SendAsync(request, slower.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new TonightBoard(null, [], $"MagicMovieNight answered {(int)response.StatusCode}.");
            }

            return Read(await response.Content.ReadAsStringAsync(slower.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new TonightBoard(null, [], "It took too long to come back with anything.");
        }
        catch (Exception error) when (error is HttpRequestException or JsonException)
        {
            logger.LogInformation(error, "Could not ask for a suggestion");
            return new TonightBoard(null, [], "Could not reach MagicMovieNight.");
        }
    }

    public async Task<TonightBoard> TonightAsync(int take = 5, CancellationToken token = default)
    {
        if (!Configured)
        {
            return new TonightBoard(null, []);
        }

        string url = $"{_options.MovieNightUrl!.TrimEnd('/')}/api/tonight?take={take}";

        try
        {
            // Reading the last answer is a database query on the other side and
            // should be quick or not at all.
            using CancellationTokenSource quick = CancellationTokenSource.CreateLinkedTokenSource(token);
            quick.CancelAfter(TimeSpan.FromSeconds(10));

            using HttpResponseMessage response = await http.GetAsync(url, quick.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // A 404 is the ordinary case while that app is on an older
                // build, not a fault worth shouting about.
                logger.LogInformation(
                    "MagicMovieNight answered {Status} for tonight's picks", (int)response.StatusCode);
                return new TonightBoard(null, []);
            }

            return Read(await response.Content.ReadAsStringAsync(quick.Token).ConfigureAwait(false));
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException)
        {
            logger.LogInformation(error, "Could not read tonight's picks");
            return new TonightBoard(null, []);
        }
    }

    private static TonightBoard Read(string body)
    {
        using JsonDocument document = JsonDocument.Parse(body);

        if (document.RootElement.String("error") is string refused)
        {
            return new TonightBoard(null, [], refused);
        }

        {
            DateTimeOffset? decided = null;
            if (document.RootElement.TryGetProperty("run", out JsonElement run)
                && run.ValueKind == JsonValueKind.Object
                && DateTimeOffset.TryParse(run.String("createdAt"), out DateTimeOffset at))
            {
                decided = at;
            }

            List<TonightPick> picks = [];
            foreach (JsonElement pick in document.RootElement.Array("picks"))
            {
                picks.Add(new TonightPick(
                    (int)(pick.Long("rank") ?? 0),
                    pick.String("title") ?? "(untitled)",
                    (int?)pick.Long("year"),
                    pick.String("kind") ?? "Movie",
                    pick.String("pitch"),
                    pick.String("basedOn"),
                    pick.String("whereToWatch"),
                    pick.String("posterUrl"),
                    pick.Bool("inLibrary"),
                    (int?)pick.Long("confidence"),
                    pick.Long("tmdbId"),
                    pick.Long("tvdbId"),
                    (int?)pick.Long("runtimeMinutes")));
            }

            return new TonightBoard(decided, picks);
        }
    }
}
