// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Notifications;

namespace WhenWillTheBus.Core.Tests;

/// <summary>
/// The two alerts that replace somebody opening the app for each school run.
/// Morning readings are 7 Oct's, when the bus waited at 3.07 miles, left at
/// 07:52 and reached the stop at 08:00:45; that afternoon the rider boarded at
/// 16:19 for a bus due home around 17:30.
/// </summary>
public sealed class RunAlertsTests
{
    private static readonly TimeSpan Eastern = TimeSpan.FromHours(-4);
    private static readonly DateTimeOffset Target = new(2026, 10, 7, 8, 1, 0, Eastern);

    private static Journey Approaching(JourneyStage stage = JourneyStage.ToStop, DateTimeOffset? target = null) =>
        new() { Stage = stage, Target = target ?? Target, JourneyId = "20261007-am" };

    private static DateTimeOffset At(int hour, int minute) => new(2026, 10, 7, hour, minute, 0, Eastern);

    /// <summary>
    /// Waiting where it always waits is not leaving. The bus stands just
    /// outside three miles for most of the approach window, and an alert from
    /// there would be the 07:16 "bus on the way" nobody can act on.
    /// </summary>
    [Fact]
    public void ParkedBus_HasNotSetOff()
    {
        RunAlert due = RunAlerts.Due(Approaching(), 3.07, At(7, 51), RunAlert.None);

        Assert.Equal(RunAlert.None, due);
    }

    [Fact]
    public void BusInsideTheParkingRing_HasSetOff()
    {
        RunAlert due = RunAlerts.Due(Approaching(), 2.68, At(7, 53), RunAlert.None);

        Assert.Equal(RunAlert.Started, due);
    }

    /// <summary>Once per journey. A buzz every thirty seconds until the bus arrives is the old bug.</summary>
    [Fact]
    public void Departure_IsSaidOnce()
    {
        RunAlert due = RunAlerts.Due(Approaching(), 1.58, At(7, 55), RunAlert.Started);

        Assert.Equal(RunAlert.None, due);
    }

    [Fact]
    public void FiveMinutesOut_IsTheSecondAlert()
    {
        RunAlert due = RunAlerts.Due(Approaching(), 1.22, At(7, 56), RunAlert.Started);

        Assert.Equal(RunAlert.FiveMinutes, due);
    }

    [Fact]
    public void SixMinutesOut_IsNotYetFive()
    {
        RunAlert due = RunAlerts.Due(Approaching(), 1.83, At(7, 55), RunAlert.Started);

        Assert.Equal(RunAlert.None, due);
    }

    /// <summary>
    /// No GPS for the last stretch -- 5 Oct went nineteen minutes without a
    /// fix -- must still start a card, or the five-minute alert arrives on a
    /// phone with nothing on its Lock Screen.
    /// </summary>
    [Fact]
    public void FiveMinutesWithNoFix_StartsTheCardToo()
    {
        RunAlert due = RunAlerts.Due(Approaching(), null, At(7, 56), RunAlert.None);

        Assert.Equal(RunAlert.Started | RunAlert.FiveMinutes, due);
    }

    [Fact]
    public void BothAtOnce_GoOutAsOneAlertInTheUrgentWording()
    {
        (string title, string body) = RunAlerts.TextFor(
            RunAlert.Started | RunAlert.FiveMinutes, Approaching(), "Willow", EngineFixtures.Clock);

        Assert.Equal("5 minutes away", title);
        Assert.Contains("8:01", body);
    }

    private static readonly DateTimeOffset HomeAround = new(2026, 10, 7, 17, 30, 0, Eastern);

    private static Journey RidingHome(DateTimeOffset? target = null) =>
        new() { Stage = JourneyStage.FromSchool, Target = target, JourneyId = "20261007-pm" };

    /// <summary>
    /// Boarding is the start of the afternoon run, wherever the bus is. It
    /// leaves school five miles out, so a distance rule would never fire, and
    /// on 7 Oct nobody opened the app and there was no card for the whole ride.
    /// </summary>
    [Fact]
    public void BoardingAtSchool_StartsTheRideHome()
    {
        RunAlert due = RunAlerts.Due(RidingHome(HomeAround), 5.8, At(16, 25), RunAlert.None);

        Assert.Equal(RunAlert.Started, due);
    }

    /// <summary>Nothing learned about the ride yet is no reason to stay quiet about boarding.</summary>
    [Fact]
    public void BoardingWithNoTimeForHome_IsStillSaid()
    {
        RunAlert due = RunAlerts.Due(RidingHome(), 5.8, At(16, 25), RunAlert.None);
        (string title, string body) = RunAlerts.TextFor(due, RidingHome(), "Willow", EngineFixtures.Clock);

        Assert.Equal(RunAlert.Started, due);
        Assert.Equal("On the bus home", title);
        Assert.Equal("Willow is aboard the bus home.", body);
    }

    [Fact]
    public void FiveMinutesFromHome_IsTheSecondAfternoonAlert()
    {
        RunAlert due = RunAlerts.Due(RidingHome(HomeAround), 1.1, At(17, 25), RunAlert.Started);
        (string title, _) = RunAlerts.TextFor(due, RidingHome(HomeAround), "Willow", EngineFixtures.Clock);

        Assert.Equal(RunAlert.FiveMinutes, due);
        Assert.Equal("5 minutes from home", title);
    }

    /// <summary>The stage alerts already cover the stop, the ride to school and home.</summary>
    [Theory]
    [InlineData(JourneyStage.AtStop)]
    [InlineData(JourneyStage.ToSchool)]
    [InlineData(JourneyStage.AtSchool)]
    [InlineData(JourneyStage.Home)]
    [InlineData(JourneyStage.Idle)]
    public void OnlyTheRideTowardsTheStop_IsAlerted(JourneyStage stage)
    {
        RunAlert due = RunAlerts.Due(Approaching(stage), 0.5, At(7, 58), RunAlert.None);

        Assert.Equal(RunAlert.None, due);
    }
}
