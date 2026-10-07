// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Notifications;

namespace WhenWillTheBus.Core.Tests;

/// <summary>
/// The widget's snapshot, now written by the app AND served by the worker.
/// A key the Swift side does not find is a widget stuck on its placeholder.
/// </summary>
public sealed class HomeSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 16, 47, 0, TimeSpan.FromHours(-4));
    private static readonly DateTimeOffset HomeAround = new(2026, 10, 7, 17, 30, 0, TimeSpan.FromHours(-4));

    private static readonly ArrivalPrediction Tomorrow = new()
    {
        Run = Run.Am,
        Arrival = new DateTimeOffset(2026, 10, 8, 8, 1, 0, TimeSpan.FromHours(-4)),
        Source = PredictionSource.Learned,
        Basis = PredictionBasis.Historical,
    };

    private static JsonElement Read(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void CarriesEveryFieldTheWidgetDecodes()
    {
        JsonElement snapshot = Read(HomeSnapshot.Serialise(
            "Willow", "2563",
            new Journey { Stage = JourneyStage.FromSchool, Target = HomeAround, Progress = 40, JourneyId = "20261007-pm" },
            Tomorrow, 5.8, Now));

        Assert.Equal("Willow", snapshot.GetProperty("riderName").GetString());
        Assert.Equal("from_school", snapshot.GetProperty("stage").GetString());
        Assert.Equal("historical", snapshot.GetProperty("basis").GetString());
        Assert.Equal(HomeAround.ToUnixTimeSeconds(), snapshot.GetProperty("target").GetInt64());
        Assert.Equal(Now.ToUnixTimeSeconds(), snapshot.GetProperty("updatedAt").GetInt64());
        Assert.Equal(40, snapshot.GetProperty("progress").GetInt32());
        Assert.Equal(5.8, snapshot.GetProperty("distanceMiles").GetDouble());
        Assert.Equal("2563", snapshot.GetProperty("busNumber").GetString());
    }

    /// <summary>Aboard with no time for home must not borrow tomorrow morning's pickup.</summary>
    [Fact]
    public void AboardWithNoTarget_ShowsNoTimeRatherThanTheNextRun()
    {
        JsonElement snapshot = Read(HomeSnapshot.Serialise(
            "Willow", null,
            new Journey { Stage = JourneyStage.FromSchool, JourneyId = "20261007-pm" },
            Tomorrow, null, Now));

        Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("target").ValueKind);
    }

    [Fact]
    public void BetweenRuns_TheNextArrivalIsShown()
    {
        JsonElement snapshot = Read(HomeSnapshot.Serialise("Willow", null, Journey.Idle, Tomorrow, null, Now));

        Assert.Equal("idle", snapshot.GetProperty("stage").GetString());
        Assert.Equal(Tomorrow.Arrival.ToUnixTimeSeconds(), snapshot.GetProperty("target").GetInt64());
    }
}
