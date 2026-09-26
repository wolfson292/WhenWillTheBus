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
    int? Confidence);

/// <summary>What was suggested, and when it was worked out.</summary>
public sealed record TonightBoard(DateTimeOffset? DecidedAt, IReadOnlyList<TonightPick> Picks);

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

    public async Task<TonightBoard> TonightAsync(int take = 5, CancellationToken token = default)
    {
        if (!Configured)
        {
            return new TonightBoard(null, []);
        }

        string url = $"{_options.MovieNightUrl!.TrimEnd('/')}/api/tonight?take={take}";

        try
        {
            using HttpResponseMessage response = await http.GetAsync(url, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // A 404 is the ordinary case while that app is on an older
                // build, not a fault worth shouting about.
                logger.LogInformation(
                    "MagicMovieNight answered {Status} for tonight's picks", (int)response.StatusCode);
                return new TonightBoard(null, []);
            }

            using JsonDocument document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));

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
                    (int?)pick.Long("confidence")));
            }

            return new TonightBoard(decided, picks);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogInformation(error, "Could not read tonight's picks");
            return new TonightBoard(null, []);
        }
    }
}
