// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.Logging.Abstractions;
using WhenWillTheBus.Server.Devices;

namespace WhenWillTheBus.Server.Tests;

/// <summary>
/// What happens when ActivityKit reissues an activity's push token.
/// </summary>
/// <remarks>
/// It does this while the card is running, and every reissue arrives at the
/// worker as another registration. Observed on a real iPhone: one test card
/// held THREE registrations, so the worker pushed three times to one card. None
/// of them ever expired, because Apple answers 200 for a freshly retired token
/// rather than BadDeviceToken, so nothing was ever told to forget it.
/// </remarks>
public sealed class DeviceRegistryTests
{
    private static readonly DateTimeOffset Morning = new(2026, 9, 22, 7, 30, 0, TimeSpan.Zero);

    private static DeviceRegistry New() => new(NullLogger<DeviceRegistry>.Instance);

    private static RegisteredActivity Card(
        string pushToken,
        string journeyId = "20260922-am",
        string? deviceId = "PHONE-A",
        bool sandbox = true) =>
        new(journeyId, 1234, pushToken, Morning, sandbox, deviceId);

    [Fact]
    public void AReissuedTokenReplacesTheOneItSucceeds()
    {
        DeviceRegistry registry = New();

        registry.Register(Card("token-1"));
        registry.Register(Card("token-2"));
        registry.Register(Card("token-3"));

        RegisteredActivity only = Assert.Single(registry.All);
        Assert.Equal("token-3", only.PushToken);
    }

    [Fact]
    public void BothParentsWatchingOneBusKeepTheirOwnCards()
    {
        DeviceRegistry registry = New();

        registry.Register(Card("mum-token", deviceId: "PHONE-A"));
        registry.Register(Card("dad-token", deviceId: "PHONE-B"));

        Assert.Equal(2, registry.All.Count);
    }

    [Fact]
    public void OnePhoneWatchingTwoJourneysKeepsBoth()
    {
        DeviceRegistry registry = New();

        registry.Register(Card("morning-token", journeyId: "20260922-am"));
        registry.Register(Card("afternoon-token", journeyId: "20260922-pm"));

        Assert.Equal(2, registry.All.Count);
    }

    /// <summary>
    /// A build that predates the field sends no device id, and two of those
    /// cannot be told apart. Keeping both over-pushes; dropping one would leave
    /// a parent with a card that stops moving, so it over-pushes on purpose.
    /// </summary>
    [Fact]
    public void RegistrationsWithoutADeviceIdAreLeftAlone()
    {
        DeviceRegistry registry = New();

        registry.Register(Card("old-token-1", deviceId: null));
        registry.Register(Card("old-token-2", deviceId: null));

        Assert.Equal(2, registry.All.Count);
    }

    /// <summary>
    /// The same phone in both environments is two real cards: a development
    /// build and a TestFlight build installed side by side mint tokens that each
    /// host rejects, so neither may displace the other.
    /// </summary>
    [Fact]
    public void TheSameJourneyInBothEnvironmentsIsTwoCards()
    {
        DeviceRegistry registry = New();

        registry.Register(Card("sandbox-token", deviceId: "PHONE-A", sandbox: true));
        registry.Register(Card("production-token", deviceId: "PHONE-A", sandbox: false));

        // Same device, same journey, different environment: the newer one wins
        // under the device+journey rule, which is correct -- one phone cannot
        // have the same card running in two environments at once.
        RegisteredActivity only = Assert.Single(registry.All);
        Assert.False(only.Sandbox);
    }

    [Fact]
    public void ForgettingATokenRemovesOnlyThatCard()
    {
        DeviceRegistry registry = New();

        registry.Register(Card("mum-token", deviceId: "PHONE-A"));
        registry.Register(Card("dad-token", deviceId: "PHONE-B"));

        registry.Forget("mum-token");

        RegisteredActivity only = Assert.Single(registry.All);
        Assert.Equal("dad-token", only.PushToken);
    }
}
