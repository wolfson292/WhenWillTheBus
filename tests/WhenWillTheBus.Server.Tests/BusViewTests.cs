// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using WhenWillTheBus.Core;
using WhenWillTheBus.Core.Api;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Prediction;
using WhenWillTheBus.Server.Api;

namespace WhenWillTheBus.Server.Tests;

/// <summary>
/// The /bus page says what the app's Bus and Map tabs say.
/// </summary>
public sealed class BusViewTests
{
    private static readonly LocalClock Clock = new(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));

    // 29 Oct, 13:00 Eastern: between the runs, the afternoon one next.
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 17, 0, 0, TimeSpan.Zero);

    private static Student Rider() => new()
    {
        ChildId = 19457624,
        Name = "Willow Eliza Wolf",
        BusNumber = "2563",
        SubstituteBus = "1743",
        SchoolName = "Cypress Lake MS",
        AmScheduled = new TimeOnly(7, 40),
        PmScheduled = new TimeOnly(17, 20),
    };

    private static JsonElement State(Student rider)
    {
        string json = BusView.State(new Dictionary<long, Student> { [rider.ChildId] = rider }, new PredictionEngine(Clock), Clock, Now);
        return JsonDocument.Parse(json).RootElement.GetProperty("riders")[0];
    }

    [Fact]
    public void WithNothingLearnedItSaysTimetableOnly()
    {
        JsonElement card = State(Rider()).GetProperty("card");

        Assert.True(card.GetProperty("estimate").GetBoolean());
        Assert.Equal("Next bus", card.GetProperty("stage").GetString());
        Assert.Equal("5:20", card.GetProperty("headline").GetProperty("text").GetString());
        Assert.Equal("PM", card.GetProperty("headline").GetProperty("meridiem").GetString());

        JsonElement confidence = card.GetProperty("confidence");
        Assert.Equal(1, confidence.GetProperty("filled").GetInt32());
        Assert.Equal("timetable only", confidence.GetProperty("label").GetString());
    }

    [Fact]
    public void ATimetableTimeNeverTicksInSeconds()
    {
        JsonElement countdown = State(Rider()).GetProperty("card").GetProperty("countdown");

        Assert.Equal("to", countdown.GetProperty("mode").GetString());
        Assert.False(countdown.GetProperty("ticking").GetBoolean());
    }

    [Fact]
    public void TheSubstituteBusIsShownAsTheAppShowsIt()
    {
        Assert.Equal("1743 (2563)", State(Rider()).GetProperty("bus").GetString());
    }

    [Fact]
    public void TheFooterCarriesTheSchoolAndTimetable()
    {
        Assert.Equal("Cypress Lake MS  ·  timetable AM 7:40 AM / PM 5:20 PM", State(Rider()).GetProperty("footer").GetString());
    }

    [Fact]
    public void WithNoReadingTheMapSaysSo()
    {
        Assert.Equal("Nothing to show yet.", State(Rider()).GetProperty("map").GetProperty("caption").GetString());
    }

    [Fact]
    public void ScansAreGroupedByTheHouseholdsDay()
    {
        Student rider = Rider() with
        {
            Scans =
            [
                // 23:30 UTC is still the 28th in Fort Myers.
                new ScanEvent(new DateTimeOffset(2026, 10, 28, 23, 30, 0, TimeSpan.Zero), "stop", ScanKind.Dropoff, null),
                new ScanEvent(new DateTimeOffset(2026, 10, 29, 11, 50, 0, TimeSpan.Zero), "stop", ScanKind.Pickup, null),
            ],
        };

        JsonElement state = State(rider);
        JsonElement days = state.GetProperty("scans");

        Assert.Equal("Today", days[0].GetProperty("day").GetString());
        Assert.Equal("Yesterday", days[1].GetProperty("day").GetString());
        Assert.Equal("7:30 PM", days[1].GetProperty("scans")[0].GetProperty("at").GetString());
        Assert.Equal("on 7:50 AM", state.GetProperty("card").GetProperty("facts").GetProperty("scans").GetString());
    }

    [Fact]
    public void ASubstitutesRunIsNotDrawnAsTheRoute()
    {
        PredictionEngine engine = new(Clock);
        Student rider = Rider();
        TrackPoint[] track = [new(600, 26.54, -81.90), new(0, 26.53, -81.88)];

        engine.LoadHistory(rider.ChildId,
        [
            new RunArrival { Run = Run.Pm, Arrival = Now.AddDays(-1), ClosestMiles = 0.02, Track = track },
            new RunArrival { Run = Run.Pm, Arrival = Now.AddDays(-2), ClosestMiles = 0.02, Track = track, Substitute = true },
        ]);

        Assert.Single(BusView.Routes(engine, rider, Run.Pm));
    }

    [Theory]
    [InlineData("", "text/html; charset=utf-8")]
    [InlineData("bus.js", "text/javascript; charset=utf-8")]
    [InlineData("leaflet.css", "text/css; charset=utf-8")]
    public void OnlyThePagesOwnFilesAreServed(string name, string type)
    {
        Assert.Equal(type, Assert.IsAssignableFrom<IContentTypeHttpResult>(WebFiles.Serve(name)).ContentType);
    }

    [Theory]
    [InlineData("../appsettings.json")]
    [InlineData("leaflet/LICENSE")]
    [InlineData("Web/bus.html")]
    public void AnythingElseIsNotFound(string name)
    {
        Assert.IsAssignableFrom<IStatusCodeHttpResult>(WebFiles.Serve(name));
        Assert.Equal(404, ((IStatusCodeHttpResult)WebFiles.Serve(name)).StatusCode);
    }
}
