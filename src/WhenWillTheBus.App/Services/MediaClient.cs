// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WhenWillTheBus.Core.Api;

namespace WhenWillTheBus.App.Services;

/// <summary>Something the family could ask to watch.</summary>
public sealed record WatchResult(
    string Kind,
    string Title,
    int? Year,
    long RemoteId,
    string? Overview,
    string? PosterUrl,
    bool AlreadyHave)
{
    public bool IsSeries => Kind == "series";

    public string Label => Year is int year ? $"{Title} ({year})" : Title;
}

/// <summary>Something that was asked for, and what came of it.</summary>
public sealed record WatchRequest(
    string Kind,
    string Title,
    int? Year,
    string? PosterUrl,
    string? RequestedBy,
    DateTimeOffset RequestedAt,
    string Outcome);

/// <summary>
/// Searching for and asking for things to watch.
/// </summary>
/// <remarks>
/// Everything goes through the worker. The phone never holds a Radarr or Sonarr
/// key, never needs to reach them, and does not know they exist — which is what
/// makes this safe to put on a phone that leaves the house, and what lets the
/// media servers stay entirely off the internet.
/// </remarks>
public sealed class MediaClient(HttpClient http, CredentialStore credentials)
{
    public async Task<IReadOnlyList<WatchResult>> SearchAsync(
        string term, string? kind = null, CancellationToken cancellationToken = default)
    {
        string query = $"q={WebUtility.UrlEncode(term)}"
            + (kind is null ? string.Empty : $"&kind={kind}");

        string? body = await GetAsync($"/media/search?{query}", cancellationToken);
        if (body is null)
        {
            return [];
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            List<WatchResult> results = [];
            foreach (JsonElement item in document.RootElement.Array("results"))
            {
                results.Add(new WatchResult(
                    item.String("kind") ?? "movie",
                    item.String("title") ?? "(untitled)",
                    (int?)item.Long("year"),
                    item.Long("remoteId") ?? 0,
                    item.String("overview"),
                    item.String("posterUrl"),
                    item.Bool("alreadyHave")));
            }

            return results;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Ask for something. Returns what to tell the person who asked.</summary>
    public async Task<string> RequestAsync(WatchResult item, CancellationToken cancellationToken = default)
    {
        (string Url, string Key)? server = await credentials.ReadServerAsync();
        if (server is null)
        {
            return "No worker is configured.";
        }

        // The identifier and enough to log. The worker rebuilds the real
        // payload from what the media server says about that identifier, so
        // nothing here is trusted for anything but the record.
        StringBuilder json = new();
        json.Append("{\"kind\":").Append(JsonSerializer.Serialize(item.Kind));
        json.Append(",\"remoteId\":").Append(item.RemoteId);
        json.Append(",\"title\":").Append(JsonSerializer.Serialize(item.Title));
        if (item.Year is int year)
        {
            json.Append(",\"year\":").Append(year);
        }

        if (item.PosterUrl is string poster)
        {
            json.Append(",\"posterUrl\":").Append(JsonSerializer.Serialize(poster));
        }

        if (DeviceIdentity.VendorId is string id)
        {
            json.Append(",\"clientId\":").Append(JsonSerializer.Serialize(id));
        }

        json.Append('}');

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, $"{server.Value.Url}/media/request")
            {
                Content = new StringContent(json.ToString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.Value.Key);

            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
            string said = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return "The worker refused that.";
            }

            using JsonDocument document = JsonDocument.Parse(said);
            return document.RootElement.String("outcome") ?? "Asked for.";
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or TaskCanceledException)
        {
            return "Could not reach the worker.";
        }
    }

    public async Task<IReadOnlyList<WatchRequest>> RecentAsync(CancellationToken cancellationToken = default)
    {
        string? body = await GetAsync("/media/requests", cancellationToken);
        if (body is null)
        {
            return [];
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            List<WatchRequest> requests = [];
            foreach (JsonElement item in document.RootElement.EnumerateArray())
            {
                DateTimeOffset at = DateTimeOffset.TryParse(item.String("requestedAt"), out DateTimeOffset when)
                    ? when
                    : DateTimeOffset.MinValue;

                requests.Add(new WatchRequest(
                    item.String("kind") ?? "movie",
                    item.String("title") ?? "(untitled)",
                    (int?)item.Long("year"),
                    item.String("posterUrl"),
                    item.String("requestedBy"),
                    at,
                    item.String("outcome") ?? string.Empty));
            }

            return requests;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<string?> GetAsync(string path, CancellationToken cancellationToken)
    {
        (string Url, string Key)? server = await credentials.ReadServerAsync();
        if (server is null)
        {
            return null;
        }

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, $"{server.Value.Url}{path}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.Value.Key);

            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync(cancellationToken)
                : null;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }
}
