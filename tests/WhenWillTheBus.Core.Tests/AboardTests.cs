// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core.Prediction;

namespace WhenWillTheBus.Core.Tests;

public sealed class AboardTests
{
    private static readonly GeoPoint School = new(40.75000, -74.10000);
    private static readonly GeoPoint MilesAway = new(40.76500, -74.10000);   // about a mile north
    private static readonly GeoPoint NextToSchool = new(40.75010, -74.10000);

    /// <summary>The ordinary afternoon: the phone left, and it left with the bus.</summary>
    [Fact]
    public void PhoneThatTravelledWithTheBus_IsAboard() =>
        Assert.True(Aboard.TravelledWithTheBus(School, MilesAway, MilesAway));

    /// <summary>
    /// §6. Proximity alone cannot tell riding from standing in a school car park
    /// while the bus loads twenty metres away — which is exactly the moment the
    /// question is asked, so proximity alone reads as aboard every afternoon.
    /// </summary>
    [Fact]
    public void PhoneStillAtSchool_IsNotAboard_EvenRightBesideTheBus() =>
        Assert.False(Aboard.TravelledWithTheBus(School, NextToSchool, NextToSchool));

    /// <summary>
    /// §6. Distance travelled alone cannot tell a bus from a lift home in a car.
    /// </summary>
    [Fact]
    public void PhoneThatTravelledWithoutTheBus_IsNotAboard() =>
        Assert.False(Aboard.TravelledWithTheBus(School, MilesAway, School));

    /// <summary>
    /// §6. A missing fix is NOT an answer of "no". It has not been shown that
    /// they are on the bus, but it has not been shown they are at school either;
    /// the caller falls back to the scan.
    /// </summary>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void AMissingFix_IsNoAnswer(bool noOrigin, bool noPhone, bool noBus) =>
        Assert.False(Aboard.TravelledWithTheBus(
            noOrigin ? null : School,
            noPhone ? null : MilesAway,
            noBus ? null : MilesAway));
}
