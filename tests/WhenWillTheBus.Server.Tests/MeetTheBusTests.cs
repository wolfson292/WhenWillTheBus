// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Server.Travel;

namespace WhenWillTheBus.Server.Tests;

public sealed class MeetTheBusTests
{
    private static readonly DateTimeOffset Due = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(2);

    private static string Clock(DateTimeOffset moment) => moment.ToString("HH:mm");

    private static MeetPlan At(int minutesBeforeBus, DriveTime drive) =>
        MeetTheBus.Decide(Due.AddMinutes(-minutesBeforeBus), Due, drive, Margin, Clock);

    private static readonly DriveTime TenInTraffic = new(600, 420, 3.1, "tomtom");

    [Fact]
    public void WithTimeToSpareItSaysWhenToLeave()
    {
        MeetPlan plan = At(20, TenInTraffic);

        Assert.Equal(MeetVerdict.Wait, plan.Verdict);
        Assert.Equal(Due.AddMinutes(-12), plan.LeaveBy);
        Assert.Equal(8, plan.LeaveInMinutes);
        Assert.Contains("Leave by 11:48, in 8 min", plan.Message);

        // How much of it is traffic is the point of asking TomTom at all.
        Assert.Contains("3 of it traffic", plan.Message);
    }

    [Fact]
    public void HoursAheadItGivesATimeNotACountdown()
    {
        MeetPlan plan = At(600, TenInTraffic);

        Assert.Contains("Leave by 11:48 (", plan.Message);
        Assert.DoesNotContain(" min (", plan.Message.Split('(')[0]);
    }

    [Fact]
    public void AtTheLeaveByTimeItSaysGo() =>
        Assert.Equal(MeetVerdict.LeaveNow, At(12, TenInTraffic).Verdict);

    /// <summary>Still ahead of the bus, but inside the margin: worth saying it is tight.</summary>
    [Fact]
    public void InsideTheMarginItSaysHurry() =>
        Assert.Equal(MeetVerdict.Hurry, At(11, TenInTraffic).Verdict);

    [Fact]
    public void WhenTheDriveIsLongerThanTheWaitItSaysSo()
    {
        MeetPlan plan = At(7, TenInTraffic);

        Assert.Equal(MeetVerdict.TooLate, plan.Verdict);
        Assert.Contains("3 min after the bus", plan.Message);
    }

    /// <summary>An empty-road time is labelled as one, so nobody trusts it like a live one.</summary>
    [Fact]
    public void AnEmptyRoadTimeSaysItIsOne() =>
        Assert.Contains("empty roads", At(30, new DriveTime(600, 600, 3.1, "osrm")).Message);

    [Fact]
    public void TomTomsAnswerIsRead()
    {
        DriveTime? drive = DriveTimes.ParseTomTom("""
            {"routes":[{"summary":{"lengthInMeters":4828,"travelTimeInSeconds":720,
              "trafficDelayInSeconds":180,"noTrafficTravelTimeInSeconds":540}}]}
            """);

        Assert.Equal(720, drive!.Seconds);
        Assert.Equal(180, drive.TrafficSeconds);
        Assert.Equal(3.0, drive.Miles, 1);
    }

    [Fact]
    public void OsrmsAnswerIsRead()
    {
        DriveTime? drive = DriveTimes.ParseOsrm("""{"code":"Ok","routes":[{"duration":612.4,"distance":4828}]}""");

        Assert.Equal(612, drive!.Seconds);
        Assert.Null(drive.TrafficSeconds);
        Assert.Equal("osrm", drive.Source);
    }

    /// <summary>Outside the roads it holds, OSRM finds nothing -- an answer of none, not a failure.</summary>
    [Fact]
    public void NoRouteIsNoDriveTime() =>
        Assert.Null(DriveTimes.ParseOsrm("""{"code":"NoRoute","routes":[]}"""));
}
