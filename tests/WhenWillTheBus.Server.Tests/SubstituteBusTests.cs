// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Notifications;
using WhenWillTheBus.Server.Monitoring;

namespace WhenWillTheBus.Server.Tests;

/// <summary>
/// A substitute bus shown and announced.
/// </summary>
/// <remarks>
/// On 28 Oct Willow rode bus 1743 in place of 2563. WheresTheBus's own app
/// showed "Bus 1743(2563)"; this one showed "Bus 2563" all day and said nothing.
/// </remarks>
public sealed class SubstituteBusTests
{
    private static readonly DateOnly Day = new(2026, 10, 28);

    private static Student Rider(string? sub) => new()
    {
        ChildId = 19457624,
        Name = "Willow Eliza Wolf",
        BusNumber = "2563",
        SubstituteBus = sub,
    };

    [Fact]
    public void TheSubstituteIsShownFirstWithTheUsualBusAfterIt()
    {
        Assert.Equal("1743 (2563)", Rider("1743").ShownBus);
        Assert.Equal("2563", Rider(null).ShownBus);

        // The same number in both fields is not a substitute.
        Assert.Equal("2563", Rider("2563").ShownBus);
    }

    [Fact]
    public void TheUsualBusStaysWhatTheRouteIsTrackedBy()
    {
        Assert.Equal("2563", Rider("1743").BusNumber);
    }

    [Fact]
    public void ASubstituteIsAnnouncedOncePerDay()
    {
        string? today = BusMonitor.SubstituteKey(Rider("1743"), Day);

        Assert.NotNull(today);
        Assert.Equal(today, BusMonitor.SubstituteKey(Rider("1743"), Day));
        Assert.NotEqual(today, BusMonitor.SubstituteKey(Rider("1743"), Day.AddDays(1)));
        Assert.NotEqual(today, BusMonitor.SubstituteKey(Rider("1810"), Day));
    }

    [Fact]
    public void NoSubstituteMeansNothingToAnnounce()
    {
        Assert.Null(BusMonitor.SubstituteKey(Rider(null), Day));
        Assert.Null(BusMonitor.SubstituteKey(Rider("2563"), Day));
    }

    [Fact]
    public void TheNoticeNamesBothBuses()
    {
        (string title, string body) = RunAlerts.SubstituteText(Rider("1743"));

        Assert.Equal("Substitute bus today", title);
        Assert.Equal("Willow Eliza Wolf is on bus 1743 today, covering for 2563.", body);
    }
}
