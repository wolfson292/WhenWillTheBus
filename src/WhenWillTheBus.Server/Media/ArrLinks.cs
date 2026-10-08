// SPDX-License-Identifier: GPL-3.0-or-later

namespace WhenWillTheBus.Server.Media;

/// <summary>
/// Links from the status page into Radarr and Sonarr.
/// </summary>
/// <remarks>
/// The worker reaches them at a LAN address with a port, which a browser can
/// use at home and nowhere else. Through the reverse proxy they sit under the
/// SAME site as this page -- https://nas.denlair.com/radarr -- so a page
/// opened through it links to its own host and scheme, under the path the
/// worker's address already carries. Opened directly, there is no better
/// guess than the worker's own address. <see cref="ArrOptions.LinkUrl"/>
/// overrides both. Pure, so each case is a test.
/// </remarks>
public static class ArrLinks
{
    /// <summary>Where a browser should go for this title.</summary>
    /// <param name="slug">The instance's slug when the title is in the library; null when it is not.</param>
    /// <param name="forwardedProto">X-Forwarded-Proto, set by the reverse proxy and absent otherwise.</param>
    /// <param name="forwardedHost">X-Forwarded-Host, likewise.</param>
    public static string? For(
        ArrOptions arr,
        MediaKind kind,
        long remoteId,
        string? slug,
        string? forwardedProto,
        string? forwardedHost)
    {
        string? root = Root(arr, forwardedProto, forwardedHost);
        if (root is null)
        {
            return null;
        }

        // In the library: its own page. Not in it: the instance's add screen,
        // searched by the same id the request carried, one click from adding.
        if (slug is not null)
        {
            return $"{root}/{(kind is MediaKind.Movie ? "movie" : "series")}/{Uri.EscapeDataString(slug)}";
        }

        return $"{root}/add/new?term={(kind is MediaKind.Movie ? "tmdb" : "tvdb")}:{remoteId}";
    }

    private static string? Root(ArrOptions arr, string? forwardedProto, string? forwardedHost)
    {
        if (!string.IsNullOrWhiteSpace(arr.LinkUrl))
        {
            return arr.LinkUrl.TrimEnd('/');
        }

        if (!Uri.TryCreate(arr.Url, UriKind.Absolute, out Uri? inside))
        {
            return null;
        }

        // Only a value that looks like a host is used. These headers come from
        // the proxy, but the page is also reachable directly, where anybody
        // holding the key could send them; a link is all they could steer.
        if (forwardedProto is "https" or "http"
            && !string.IsNullOrWhiteSpace(forwardedHost)
            && Uri.CheckHostName(forwardedHost.Split(':')[0]) != UriHostNameType.Unknown)
        {
            return $"{forwardedProto}://{forwardedHost}{inside.AbsolutePath.TrimEnd('/')}";
        }

        return arr.Url.TrimEnd('/');
    }
}
