// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Prediction;

namespace WhenWillTheBus.Core.Tests;

public sealed class BusStatusTests
{
    [Theory]
    [InlineData("current", BusStatusKind.Current, 0)]
    [InlineData("Current", BusStatusKind.Current, 0)]
    [InlineData("current location", BusStatusKind.Current, 0)]
    [InlineData("inactive", BusStatusKind.Inactive, null)]
    [InlineData("3 min. ago", BusStatusKind.Stale, 3)]
    [InlineData("12 mins ago", BusStatusKind.Stale, 12)]
    [InlineData("1 min ago", BusStatusKind.Stale, 1)]
    public void Parse_ReadsTheVocabularyTheApiUses(string message, BusStatusKind kind, int? age)
    {
        (BusStatusKind actualKind, int? actualAge) = BusStatus.Parse(message);

        Assert.Equal(kind, actualKind);
        Assert.Equal(age, actualAge);
    }

    /// <summary>
    /// §2. Do not crash on new vocabulary. The raw string is kept for display
    /// either way, so an unrecognised word costs nothing but a cautious answer.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("somebody rewrote this string")]
    public void Parse_TreatsUnknownVocabularyAsUnknown(string? message)
    {
        (BusStatusKind kind, int? age) = BusStatus.Parse(message);

        Assert.Equal(BusStatusKind.Unknown, kind);
        Assert.Null(age);
    }
}
