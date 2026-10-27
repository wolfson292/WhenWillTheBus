// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Prediction;

namespace WhenWillTheBus.Server.Cleanup;

/// <summary>
/// Puts a recorded run back on the roads, with the household's own OSRM.
/// </summary>
/// <remarks>
/// A fix every thirty seconds is a fifth of a mile apart at speed, so the raw
/// track cuts every corner through whatever stands on it. OSRM's map matching
/// finds the road sequence that best explains the fixes, timing included, and
/// returns the line along it.
///
/// LOCAL ON PURPOSE. The route is a child's journey between home and school;
/// it goes to the OSRM container on the same box, and nowhere else.
/// </remarks>
public sealed class RoadMatcher(HttpClient http, IOptions<CleanupOptions> options)
{
    /// <summary>
    /// How far a fix may be from the road it is matched to, in metres. Bus GPS
    /// here is good to a lane or two, but a stale fix drifts.
    /// </summary>
    private const int RadiusMetres = 30;

    public bool Enabled => !string.IsNullOrWhiteSpace(options.Value.RoadsUrl);

    /// <summary>
    /// The road driven, and how sure the match is. An empty road with
    /// confidence 0 means the fixes matched nothing, which is an answer too.
    /// </summary>
    public async Task<(IReadOnlyList<GeoPoint> Road, double Confidence)> MatchAsync(
        RunArrival arrival, CancellationToken token)
    {
        // Oldest first, one per instant: OSRM wants timestamps that increase.
        List<TrackPoint> fixes = [.. arrival.Track
            .GroupBy(point => point.SecondsBeforeArrival)
            .Select(group => group.First())
            .OrderByDescending(point => point.SecondsBeforeArrival)];

        if (fixes.Count < 2)
        {
            return ([], 0);
        }

        long arrived = arrival.Arrival.ToUnixTimeSeconds();
        string coordinates = string.Join(';', fixes.Select(point => string.Create(
            CultureInfo.InvariantCulture, $"{point.Longitude:F5},{point.Latitude:F5}")));
        string timestamps = string.Join(';', fixes.Select(point => (arrived - point.SecondsBeforeArrival).ToString(CultureInfo.InvariantCulture)));
        string radiuses = string.Join(';', fixes.Select(_ => RadiusMetres.ToString(CultureInfo.InvariantCulture)));

        // gaps=split: a lost signal is a break in the line, not a road invented
        // across it. tidy: drops the extra fixes a bus waiting at a light leaves.
        Uri url = new($"{options.Value.RoadsUrl!.TrimEnd('/')}/match/v1/driving/{coordinates}"
            + $"?timestamps={timestamps}&radiuses={radiuses}&overview=full&geometries=geojson&gaps=split&tidy=true");

        using HttpResponseMessage response = await http.GetAsync(url, token).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        JsonElement root = document.RootElement;

        // NoMatch is OSRM's answer for fixes it could not put on any road.
        string code = root.TryGetProperty("code", out JsonElement value) ? value.GetString() ?? string.Empty : string.Empty;
        if (code == "NoMatch")
        {
            return ([], 0);
        }

        if (code != "Ok")
        {
            throw new HttpRequestException($"OSRM answered {code} ({(int)response.StatusCode})");
        }

        List<GeoPoint> road = [];
        double weighted = 0;
        double total = 0;
        foreach (JsonElement matching in root.GetProperty("matchings").EnumerateArray())
        {
            double distance = matching.GetProperty("distance").GetDouble();
            weighted += matching.GetProperty("confidence").GetDouble() * distance;
            total += distance;

            foreach (JsonElement point in matching.GetProperty("geometry").GetProperty("coordinates").EnumerateArray())
            {
                road.Add(new GeoPoint(point[1].GetDouble(), point[0].GetDouble()));
            }
        }

        // By distance, so a long confident stretch is not outvoted by a scrap
        // matched in a car park.
        return (road, total > 0 ? weighted / total : 0);
    }
}
