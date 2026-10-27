// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using Microsoft.Extensions.Options;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Prediction;
using WhenWillTheBus.Server.Cleanup;

namespace WhenWillTheBus.Server.Tests;

public sealed class TraceCleanupTests
{
    private sealed class Canned(string body) : HttpMessageHandler
    {
        public Uri? Asked { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Asked = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private static RunArrival Arrival() => new()
    {
        Run = Run.Am,
        Arrival = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000),
        ClosestMiles = 0.1,

        // Out of order, and one instant twice: OSRM wants strictly increasing times.
        Track =
        [
            new TrackPoint(60, 26.45284, -81.82350),
            new TrackPoint(120, 26.45405, -81.82277),
            new TrackPoint(120, 26.45405, -81.82277),
            new TrackPoint(0, 26.45274, -81.82730),
        ],
    };

    private static RoadMatcher Matcher(Canned handler) =>
        new(new HttpClient(handler), Options.Create(new CleanupOptions { RoadsUrl = "http://roads:5005/" }));

    [Fact]
    public async Task AMatchComesBackAsTheRoadWithItsConfidence()
    {
        Canned osrm = new("""
            {"code":"Ok","matchings":[
              {"confidence":1.0,"distance":300,"geometry":{"coordinates":[[-81.82277,26.45405],[-81.82181,26.4529]]}},
              {"confidence":0.5,"distance":100,"geometry":{"coordinates":[[-81.8235,26.45284],[-81.8273,26.45274]]}}]}
            """);

        (IReadOnlyList<GeoPoint> road, double confidence) = await Matcher(osrm).MatchAsync(Arrival(), CancellationToken.None);

        Assert.Equal(4, road.Count);
        Assert.Equal(new GeoPoint(26.4529, -81.82181), road[1]);

        // Weighted by distance: 300 m at 1.0 and 100 m at 0.5.
        Assert.Equal(0.875, confidence, 3);

        // Oldest fix first, each instant once, timed from the arrival backwards.
        string query = osrm.Asked!.Query;
        Assert.Contains("timestamps=1799999880;1799999940;1800000000&", query);
        Assert.StartsWith("http://roads:5005/match/v1/driving/-81.82277,26.45405;", osrm.Asked.ToString());
    }

    /// <summary>Fixes that fit no road are an answer, recorded so the run is not retried forever.</summary>
    [Fact]
    public async Task NoMatchIsAnEmptyRoadAtZero()
    {
        (IReadOnlyList<GeoPoint> road, double confidence) =
            await Matcher(new Canned("""{"code":"NoMatch"}""")).MatchAsync(Arrival(), CancellationToken.None);

        Assert.Empty(road);
        Assert.Equal(0, confidence);
    }

    [Fact]
    public async Task AnyOtherAnswerIsAFailureToRetry()
    {
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Matcher(new Canned("""{"code":"InvalidUrl"}""")).MatchAsync(Arrival(), CancellationToken.None));
    }

    [Theory]
    [InlineData("""{"verdict":"detour","note":"Went round by the shops."}""", RunVerdict.Detour)]
    [InlineData("""{"verdict":"bad_gps","note":"The bus stopped reporting."}""", RunVerdict.BadGps)]
    [InlineData("""{"verdict":"usual","note":"Nothing unusual."}""", RunVerdict.Usual)]
    public void AReviewIsReadFromItsAnswer(string json, RunVerdict expected) =>
        Assert.Equal(expected, RunReviewer.Parse(json)?.Verdict);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"verdict":"maybe","note":"?"}""")]
    public void AnythingElseIsNoReview(string? json) => Assert.Null(RunReviewer.Parse(json));
}
