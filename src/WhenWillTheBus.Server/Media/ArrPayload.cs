// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using System.Text.Json;
using WhenWillTheBus.Core.Api;

namespace WhenWillTheBus.Server.Media;

/// <summary>
/// What gets sent to Radarr or Sonarr, and what comes back when they refuse.
/// </summary>
/// <remarks>
/// Pure, and separated from the client that posts it, because this is where a
/// wrong quality profile or root folder would live -- and both are mistakes
/// that succeed. The request is accepted, the download starts, and the first
/// sign of trouble is a disk filling with Ultra-HD or a film appearing in the
/// wrong library weeks later.
/// </remarks>
public static class ArrPayload
{
    /// <summary>
    /// The add body Radarr or Sonarr is given.
    /// </summary>
    /// <remarks>
    /// Written by hand so every field is visible. The two APIs differ in more
    /// than their names — a film has a minimum availability and a series has a
    /// monitoring policy and season folders — and a shared shape with holes in
    /// it would hide that rather than handle it.
    /// </remarks>
    public static string For(
        MediaKind kind, JsonElement item, string idField, long remoteId, string root, int profile)
    {
        StringBuilder json = new();
        json.Append('{');
        json.Append("\"title\":").Append(JsonSerializer.Serialize(item.String("title") ?? string.Empty));
        json.Append(",\"").Append(idField).Append("\":").Append(remoteId);
        json.Append(",\"qualityProfileId\":").Append(profile);
        json.Append(",\"rootFolderPath\":").Append(JsonSerializer.Serialize(root));
        json.Append(",\"monitored\":true");

        if (item.Long("year") is long year)
        {
            json.Append(",\"year\":").Append(year);
        }

        if (kind is MediaKind.Movie)
        {
            // "released" rather than "announced": a film that does not exist
            // yet otherwise sits in the queue being searched for indefinitely.
            json.Append(",\"minimumAvailability\":\"released\"");
            json.Append(",\"addOptions\":{\"searchForMovie\":true}");
        }
        else
        {
            json.Append(",\"seasonFolder\":true");
            if (item.String("titleSlug") is string slug)
            {
                json.Append(",\"titleSlug\":").Append(JsonSerializer.Serialize(slug));
            }

            json.Append(",\"addOptions\":{\"monitor\":\"all\",\"searchForMissingEpisodes\":true}");
        }

        json.Append('}');
        return json.ToString();
    }

    /// <summary>Turn an instance's refusal into something worth reading on a phone.</summary>
    public static string Explain(string body, int status)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement first = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().FirstOrDefault()
                : document.RootElement;

            string? message = first.String("errorMessage") ?? first.String("message");
            if (!string.IsNullOrWhiteSpace(message))
            {
                return Clip(message, 160);
            }
        }
        catch (JsonException)
        {
            // Not JSON. Falls through to the status code, which at least says
            // something true.
        }

        return $"The media server refused it ({status}).";
    }

    private static string Clip(string text, int limit) =>
        text.Length <= limit ? text : text[..limit] + "\u2026";
}
