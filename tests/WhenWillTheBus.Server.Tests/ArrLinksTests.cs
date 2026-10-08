// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Server.Media;

namespace WhenWillTheBus.Server.Tests;

/// <summary>Links from the status page into Radarr and Sonarr.</summary>
public sealed class ArrLinksTests
{
    private static ArrOptions Radarr(string? link = null) =>
        new() { Url = "http://192.168.3.151:7878/radarr", ApiKey = "k", LinkUrl = link };

    /// <summary>
    /// Through SWAG the page is on nas.denlair.com, and so is Radarr -- a LAN
    /// address with a port would be a dead link from anywhere but home.
    /// </summary>
    [Fact]
    public void ThroughTheProxy_LinksToTheSameSite() =>
        Assert.Equal(
            "https://nas.denlair.com/radarr/movie/paddington-2",
            ArrLinks.For(Radarr(), MediaKind.Movie, 346648, "paddington-2", "https", "nas.denlair.com"));

    [Fact]
    public void OpenedDirectly_LinksToTheWorkersOwnAddress() =>
        Assert.Equal(
            "http://192.168.3.151:7878/radarr/movie/paddington-2",
            ArrLinks.For(Radarr(), MediaKind.Movie, 346648, "paddington-2", null, null));

    [Fact]
    public void ASetLinkUrl_WinsOverBoth() =>
        Assert.Equal(
            "https://radarr.example/movie/paddington-2",
            ArrLinks.For(Radarr("https://radarr.example/"), MediaKind.Movie, 346648, "paddington-2", "https", "nas.denlair.com"));

    /// <summary>Not in the library: one click from adding it, searched by the id that was asked for.</summary>
    [Fact]
    public void NotInTheLibrary_LinksToTheAddScreen()
    {
        ArrOptions sonarr = new() { Url = "http://192.168.3.151:8989/sonarr", ApiKey = "k" };

        Assert.Equal(
            "https://nas.denlair.com/sonarr/add/new?term=tvdb:353546",
            ArrLinks.For(sonarr, MediaKind.Series, 353546, null, "https", "nas.denlair.com"));
    }

    /// <summary>
    /// The page is reachable directly too, where the headers are whatever the
    /// caller sent. Something that is not a host name is not used as one.
    /// </summary>
    [Fact]
    public void AForwardedHostThatIsNotAHost_IsIgnored() =>
        Assert.Equal(
            "http://192.168.3.151:7878/radarr/movie/paddington-2",
            ArrLinks.For(Radarr(), MediaKind.Movie, 346648, "paddington-2", "https", "evil\"><script>"));

    [Fact]
    public void ASlugIsEscapedIntoThePath() =>
        Assert.Equal(
            "https://nas.denlair.com/radarr/movie/a%2Fb",
            ArrLinks.For(Radarr(), MediaKind.Movie, 1, "a/b", "https", "nas.denlair.com"));
}
