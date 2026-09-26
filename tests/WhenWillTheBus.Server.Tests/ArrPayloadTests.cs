// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using WhenWillTheBus.Server.Media;

namespace WhenWillTheBus.Server.Tests;

/// <summary>
/// The body sent to Radarr and Sonarr.
/// </summary>
/// <remarks>
/// Tested because every mistake available here SUCCEEDS. A wrong quality
/// profile is accepted and downloads Ultra-HD; a wrong root folder is accepted
/// and files it somewhere nobody looks. Nothing fails, and the first symptom is
/// weeks away.
/// </remarks>
public sealed class ArrPayloadTests
{
    private static JsonElement Lookup(string json) => JsonDocument.Parse(json).RootElement;

    private static JsonElement Film => Lookup(
        """{"title":"Paddington 2","year":2017,"tmdbId":346648,"id":0}""");

    private static JsonElement Show => Lookup(
        """{"title":"Bluey","year":2018,"tvdbId":353546,"titleSlug":"bluey","id":0}""");

    private static JsonElement Body(string payload) => JsonDocument.Parse(payload).RootElement;

    [Fact]
    public void AFilmCarriesItsTmdbIdAndWhereToPutIt()
    {
        JsonElement body = Body(ArrPayload.For(MediaKind.Movie, Film, "tmdbId", 346648, "/data/Movies", 6));

        Assert.Equal(346648, body.GetProperty("tmdbId").GetInt64());
        Assert.Equal("/data/Movies", body.GetProperty("rootFolderPath").GetString());
        Assert.Equal(6, body.GetProperty("qualityProfileId").GetInt32());
        Assert.True(body.GetProperty("monitored").GetBoolean());
    }

    /// <summary>
    /// "released", not "announced". A film that does not exist yet otherwise
    /// sits in the queue being searched for indefinitely.
    /// </summary>
    [Fact]
    public void AFilmIsOnlyWantedOnceItExists()
    {
        JsonElement body = Body(ArrPayload.For(MediaKind.Movie, Film, "tmdbId", 346648, "/data/Movies", 6));

        Assert.Equal("released", body.GetProperty("minimumAvailability").GetString());
        Assert.True(body.GetProperty("addOptions").GetProperty("searchForMovie").GetBoolean());
    }

    /// <summary>A series is keyed on TVDB, not TMDB, and the two are different numbers.</summary>
    [Fact]
    public void ASeriesCarriesItsTvdbIdAndSeasonFolders()
    {
        JsonElement body = Body(ArrPayload.For(MediaKind.Series, Show, "tvdbId", 353546, "/data/TV", 6));

        Assert.Equal(353546, body.GetProperty("tvdbId").GetInt64());
        Assert.Equal("/data/TV", body.GetProperty("rootFolderPath").GetString());
        Assert.True(body.GetProperty("seasonFolder").GetBoolean());
        Assert.Equal("all", body.GetProperty("addOptions").GetProperty("monitor").GetString());
    }

    /// <summary>A film has no monitoring policy and a series has no availability.</summary>
    [Fact]
    public void TheTwoShapesDoNotBorrowEachOthersFields()
    {
        JsonElement film = Body(ArrPayload.For(MediaKind.Movie, Film, "tmdbId", 346648, "/data/Movies", 6));
        JsonElement show = Body(ArrPayload.For(MediaKind.Series, Show, "tvdbId", 353546, "/data/TV", 6));

        Assert.False(film.TryGetProperty("seasonFolder", out _));
        Assert.False(show.TryGetProperty("minimumAvailability", out _));
    }

    /// <summary>A title with a quote in it must not produce a body the server rejects.</summary>
    [Fact]
    public void AwkwardTitlesSurviveBeingSerialised()
    {
        JsonElement awkward = Lookup(
            """{"title":"Ocean's Eleven \"Remastered\"","year":2001,"tmdbId":161,"id":0}""");

        JsonElement body = Body(ArrPayload.For(MediaKind.Movie, awkward, "tmdbId", 161, "/data/Movies", 6));

        Assert.Equal("Ocean's Eleven \"Remastered\"", body.GetProperty("title").GetString());
    }

    [Fact]
    public void AMissingYearIsLeftOutRatherThanSentAsZero()
    {
        JsonElement unreleased = Lookup("""{"title":"Paddington 4","tmdbId":1670528,"id":0}""");

        Assert.False(Body(ArrPayload.For(MediaKind.Movie, unreleased, "tmdbId", 1670528, "/data/Movies", 6))
            .TryGetProperty("year", out _));
    }

    /// <summary>
    /// The instances explain themselves well when they refuse, and the
    /// explanation is the actionable part. Inventing a summary would throw it
    /// away.
    /// </summary>
    [Fact]
    public void ARefusalIsPassedThroughInTheServersOwnWords()
    {
        Assert.Equal(
            "This movie has already been added",
            ArrPayload.Explain("""[{"errorMessage":"This movie has already been added"}]""", 400));

        Assert.Equal(
            "Root folder does not exist",
            ArrPayload.Explain("""{"message":"Root folder does not exist"}""", 400));
    }

    [Fact]
    public void ARefusalThatIsNotJsonStillSaysSomethingTrue()
    {
        Assert.Contains("500", ArrPayload.Explain("<html>Bad Gateway</html>", 500), StringComparison.Ordinal);
    }
}
