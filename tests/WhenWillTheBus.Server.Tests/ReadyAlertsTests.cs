// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WhenWillTheBus.Server.Media;

namespace WhenWillTheBus.Server.Tests;

/// <summary>Telling somebody that what they asked for can be watched.</summary>
public sealed class ReadyAlertsTests
{
    private static readonly DateTimeOffset Asked = new(2026, 10, 7, 21, 30, 0, TimeSpan.Zero);

    private static MediaRequest Film(string? forId = null) =>
        new(MediaKind.Movie, "Paddington 2", 2017, 346648, null, "Scott iPhone", Asked, "Added")
        {
            RequestedById = "SCOTT",
            ForId = forId,
        };

    private static MediaRequest Show(DateTimeOffset? started = null) =>
        new(MediaKind.Series, "Bluey", 2018, 353546, null, "Angela iPhone 18", Asked, "Added")
        {
            RequestedById = "ANGELA",
            StartedAt = started,
        };

    [Fact]
    public void NothingOnDisk_IsNotNews() =>
        Assert.Equal(ReadyStep.None, ReadyAlerts.Next(Film(), new Readiness(0, false)));

    [Fact]
    public void AFilmWithAFile_IsReady() =>
        Assert.Equal(ReadyStep.Ready, ReadyAlerts.Next(Film(), new Readiness(1, true)));

    /// <summary>A season lands over hours. The first episodes are worth one message, not one each.</summary>
    [Fact]
    public void ASeriesArriving_IsSaidOnceThenWaitsForTheRest()
    {
        Assert.Equal(ReadyStep.Started, ReadyAlerts.Next(Show(), new Readiness(3, false)));
        Assert.Equal(ReadyStep.None, ReadyAlerts.Next(Show(started: Asked), new Readiness(5, false)));
        Assert.Equal(ReadyStep.Ready, ReadyAlerts.Next(Show(started: Asked), new Readiness(52, true)));
    }

    [Fact]
    public void ASeriesAlreadyComplete_IsOneMessage()
    {
        (string title, string body) = ReadyAlerts.TextFor(ReadyStep.Ready, Show(), new Readiness(52, true));

        Assert.Equal(ReadyStep.Ready, ReadyAlerts.Next(Show(), new Readiness(52, true)));
        Assert.Equal("Ready to watch", title);
        Assert.Equal("Bluey (2018) is ready to watch.", body);
    }

    [Fact]
    public void AskedForOnSomebodysBehalf_SaysWhoAsked()
    {
        (_, string body) = ReadyAlerts.TextFor(ReadyStep.Ready, Film(forId: "ANGELA"), new Readiness(1, true));

        Assert.Equal("Paddington 2 (2017) is ready to watch. Scott iPhone asked for it for you.", body);
    }

    [Fact]
    public void AskedForYourself_DoesNotSayYouAsked()
    {
        (_, string body) = ReadyAlerts.TextFor(ReadyStep.Ready, Film(), new Readiness(1, true));

        Assert.DoesNotContain("asked", body);
    }

    [Fact]
    public void Radarr_ReadyMeansHasAFile()
    {
        JsonElement movie = JsonDocument.Parse("""{"title":"Paddington 2","hasFile":true}""").RootElement;

        Assert.Equal(new Readiness(1, true), ArrPayload.ReadinessOf(MediaKind.Movie, movie));
    }

    /// <summary>Sonarr's own percentage, so a series still airing is complete once it has caught up.</summary>
    [Theory]
    [InlineData(0, 0.0, 0, false)]
    [InlineData(3, 25.0, 3, false)]
    [InlineData(12, 100.0, 12, true)]
    public void Sonarr_CompleteMeansCaughtUp(int files, double percent, int expectedFiles, bool complete)
    {
        JsonElement series = JsonDocument.Parse(
            $$$"""{"title":"Bluey","statistics":{"episodeFileCount":{{{files}}},"percentOfEpisodes":{{{percent}}}}}""")
            .RootElement;

        Assert.Equal(new Readiness(expectedFiles, complete), ArrPayload.ReadinessOf(MediaKind.Series, series));
    }

    /// <summary>Who to tell, and that it survives a restart: lost, every request goes quiet.</summary>
    [Fact]
    public async Task TheLogKeepsWhoItIsForAcrossARestart()
    {
        string path = Path.Combine(Path.GetTempPath(), $"wwtb-requests-{Guid.NewGuid():N}.json");
        try
        {
            RequestLog before = new(NullLogger<RequestLog>.Instance);
            await before.LoadAsync(path, 200);
            before.Record(Film(forId: "ANGELA") with { RequestedFor = "Angela iPhone 18" });
            await before.SaveAsync();

            RequestLog after = new(NullLogger<RequestLog>.Instance);
            await after.LoadAsync(path, 200);

            MediaRequest waiting = Assert.Single(after.Waiting(Asked.AddHours(1)));
            Assert.Equal("ANGELA", waiting.RecipientId);
            Assert.Equal("Angela iPhone 18", waiting.RequestedFor);
            Assert.Equal("SCOTT", waiting.RequestedById);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadyOrUnaddressedOrOld_IsNoLongerWatched()
    {
        RequestLog log = new(NullLogger<RequestLog>.Instance);
        log.Record(Film() with { ReadyAt = Asked });
        log.Record(Film() with { RequestedById = null });
        log.Record(Film() with { RequestedAt = Asked - RequestLog.WatchFor - TimeSpan.FromDays(1) });

        Assert.Empty(log.Waiting(Asked));
    }
}
