// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WhenWillTheBus.Core.Prediction;

namespace WhenWillTheBus.Server.Travel;

/// <summary>How long a drive takes, and what that answer rests on.</summary>
/// <param name="Seconds">The drive, as best known: with traffic where there is traffic.</param>
/// <param name="EmptyRoadSeconds">The same drive with no traffic, where known.</param>
/// <param name="Source">"tomtom" for live traffic, "osrm" for empty roads.</param>
public sealed record DriveTime(int Seconds, int? EmptyRoadSeconds, double Miles, string Source)
{
    /// <summary>How much today's traffic adds, where it is known.</summary>
    public int? TrafficSeconds => Source == "tomtom" && EmptyRoadSeconds is int empty
        ? Math.Max(0, Seconds - empty)
        : null;
}

/// <summary>
/// One drive time, fetched when asked for and not otherwise.
/// </summary>
/// <remarks>
/// TomTom has the traffic; the household's OSRM has the roads and answers
/// instantly, privately and without limit, but knows nothing of today. So
/// TomTom is asked first when there is a key, and OSRM answers when there is
/// not one or TomTom does not -- an empty-road time, labelled as one, beats no
/// answer at 7:50 in the morning.
///
/// Both URLs carry the two places, and TomTom's carries the key, so neither
/// client's request logging is allowed below Warning.
/// </remarks>
public sealed class DriveTimes(HttpClient http, IOptions<TravelOptions> options, ILogger<DriveTimes> logger)
{
    public async Task<DriveTime?> BetweenAsync(GeoPoint from, GeoPoint to, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(options.Value.TomTomApiKey))
        {
            try
            {
                if (await TomTomAsync(from, to, token).ConfigureAwait(false) is DriveTime live)
                {
                    return live;
                }
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
            {
                logger.LogWarning("TomTom unavailable, using empty-road time: {Message}", error.Message);
            }
        }

        if (!string.IsNullOrWhiteSpace(options.Value.RoadsUrl))
        {
            try
            {
                return await OsrmAsync(from, to, token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
            {
                logger.LogWarning("Roads server unavailable: {Message}", error.Message);
            }
        }

        return null;
    }

    private async Task<DriveTime?> TomTomAsync(GeoPoint from, GeoPoint to, CancellationToken token)
    {
        // computeTravelTimeFor=all adds the no-traffic time, which is what lets
        // the answer say how much of the drive is today's traffic.
        Uri url = new(string.Create(CultureInfo.InvariantCulture,
            $"https://api.tomtom.com/routing/1/calculateRoute/{from.Latitude:F5},{from.Longitude:F5}:{to.Latitude:F5},{to.Longitude:F5}/json")
            + $"?key={Uri.EscapeDataString(options.Value.TomTomApiKey!)}&traffic=true&travelMode=car&routeType=fastest&departAt=now&computeTravelTimeFor=all");

        using HttpResponseMessage response = await http.GetAsync(url, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return ParseTomTom(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
    }

    private async Task<DriveTime?> OsrmAsync(GeoPoint from, GeoPoint to, CancellationToken token)
    {
        Uri url = new(string.Create(CultureInfo.InvariantCulture,
            $"{options.Value.RoadsUrl!.TrimEnd('/')}/route/v1/driving/{from.Longitude:F5},{from.Latitude:F5};{to.Longitude:F5},{to.Latitude:F5}?overview=false"));

        using HttpResponseMessage response = await http.GetAsync(url, token).ConfigureAwait(false);
        return ParseOsrm(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
    }

    public static DriveTime? ParseTomTom(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("routes", out JsonElement routes) || routes.GetArrayLength() == 0)
        {
            return null;
        }

        JsonElement summary = routes[0].GetProperty("summary");
        int seconds = summary.GetProperty("travelTimeInSeconds").GetInt32();
        int? empty = summary.TryGetProperty("noTrafficTravelTimeInSeconds", out JsonElement free) ? free.GetInt32() : null;
        double miles = summary.GetProperty("lengthInMeters").GetDouble() / 1609.344;
        return new DriveTime(seconds, empty, miles, "tomtom");
    }

    /// <summary>OSRM's answer; null where it found no route, which outside its roads it will not.</summary>
    public static DriveTime? ParseOsrm(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.GetProperty("code").GetString() != "Ok" || root.GetProperty("routes").GetArrayLength() == 0)
        {
            return null;
        }

        JsonElement route = root.GetProperty("routes")[0];
        int seconds = (int)Math.Round(route.GetProperty("duration").GetDouble());
        return new DriveTime(seconds, seconds, route.GetProperty("distance").GetDouble() / 1609.344, "osrm");
    }
}
