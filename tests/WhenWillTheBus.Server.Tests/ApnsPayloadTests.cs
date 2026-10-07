// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Notifications;
using WhenWillTheBus.Server.Apns;

namespace WhenWillTheBus.Server.Tests;

/// <summary>
/// A push-to-start that Apple accepts and the phone cannot decode is a 200
/// and an empty Lock Screen, with nothing logged anywhere. These pin the shape.
/// </summary>
public sealed class ApnsPayloadTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 7, 52, 30, TimeSpan.FromHours(-4));

    private static BusActivityState State() => BusActivityState.For(
        new Journey { Stage = JourneyStage.ToStop, Target = Now.AddMinutes(8), JourneyId = "20261007-am" },
        null, 2.68, 0, Now);

    private static JsonElement Aps(string payload) =>
        JsonDocument.Parse(payload).RootElement.GetProperty("aps");

    [Fact]
    public void AStart_NamesTheSwiftTypeAndCarriesItsAttributes()
    {
        JsonElement aps = Aps(ApnsClient.BuildPayload(
            "start", State(), null, ("Bus is on the way", "Willow's bus has set off."), null,
            new BusActivityIdentity("20261007-am", "Willow", null, 18279837), sound: true));

        Assert.Equal("start", aps.GetProperty("event").GetString());
        Assert.Equal("BusActivityAttributes", aps.GetProperty("attributes-type").GetString());

        JsonElement attributes = aps.GetProperty("attributes");
        Assert.Equal("20261007-am", attributes.GetProperty("journeyId").GetString());
        Assert.Equal("Willow", attributes.GetProperty("riderName").GetString());
        Assert.Equal(18279837, attributes.GetProperty("childId").GetInt64());

        // Not optional in Swift: a missing key fails the whole decode.
        Assert.Equal(string.Empty, attributes.GetProperty("busNumber").GetString());

        Assert.Equal("default", aps.GetProperty("alert").GetProperty("sound").GetString());
        Assert.Equal("to_stop", aps.GetProperty("content-state").GetProperty("stage").GetString());
    }

    /// <summary>An update addresses a card that already has its identity.</summary>
    [Fact]
    public void AnUpdate_CarriesNoAttributes()
    {
        JsonElement aps = Aps(ApnsClient.BuildPayload("update", State(), null, null, null));

        Assert.False(aps.TryGetProperty("attributes", out _));
        Assert.False(aps.TryGetProperty("attributes-type", out _));
        Assert.False(aps.TryGetProperty("alert", out _));
    }
}
