// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.Logging.Abstractions;
using WhenWillTheBus.Server.Devices;

namespace WhenWillTheBus.Server.Tests;

/// <summary>
/// Who gets told there is an update.
/// </summary>
/// <remarks>
/// The cost of being wrong is asymmetric and both directions are bad: telling
/// nobody means the fix sits in TestFlight uninstalled, and telling the wrong
/// person means notifying somebody several times a day about a build they made
/// themselves.
/// </remarks>
public sealed class ReleaseTrackerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 10, 30, 0, TimeSpan.Zero);

    private static ReleaseTracker New() => new(NullLogger<ReleaseTracker>.Instance);

    [Fact]
    public void WithNothingAnnouncedNobodyIsBehind()
    {
        ReleaseTracker tracker = New();

        Assert.Null(tracker.Latest);
        Assert.False(tracker.IsBehind("202609221211"));
        Assert.False(tracker.IsBehind(null));
    }

    [Fact]
    public async Task APhoneOnAnOlderBuildIsBehind()
    {
        ReleaseTracker tracker = New();
        await tracker.AnnounceAsync("202609261030", Now);

        Assert.True(tracker.IsBehind("202609221211"));
        Assert.False(tracker.IsBehind("202609261030"));
    }

    /// <summary>
    /// A DEVELOPMENT BUILD REPORTS "1" FOR EVER. Without this the person who
    /// just built and uploaded the thing is told to go and install it, every
    /// time, on the phone they built it with.
    /// </summary>
    [Fact]
    public async Task ADevelopmentBuildIsNeverBehind()
    {
        ReleaseTracker tracker = New();
        await tracker.AnnounceAsync("202609261030", Now);

        Assert.False(tracker.IsBehind("1"));
    }

    /// <summary>
    /// A phone that has not said which build it runs is UNKNOWN, not behind.
    /// Nagging somebody on a guess is worse than staying quiet.
    /// </summary>
    [Fact]
    public async Task ASilentPhoneIsNotAssumedToBeBehind()
    {
        ReleaseTracker tracker = New();
        await tracker.AnnounceAsync("202609261030", Now);

        Assert.False(tracker.IsBehind(null));
        Assert.False(tracker.IsBehind("   "));
    }

    /// <summary>
    /// "Different", not "older". Pulling a bad build and re-announcing an
    /// earlier one still leaves everybody with something to install.
    /// </summary>
    [Fact]
    public async Task ARolledBackBuildStillCountsAsSomethingToInstall()
    {
        ReleaseTracker tracker = New();
        await tracker.AnnounceAsync("202609261030", Now);
        await tracker.AnnounceAsync("202609221211", Now.AddHours(1));

        Assert.Equal("202609221211", tracker.Latest);
        Assert.True(tracker.IsBehind("202609261030"));
    }

    [Fact]
    public async Task ARestartDoesNotForgetWhatIsCurrent()
    {
        string path = Path.Combine(Path.GetTempPath(), $"wwtb-release-{Guid.NewGuid():N}.json");
        try
        {
            ReleaseTracker before = New();
            await before.LoadAsync(path);
            await before.AnnounceAsync("202609261030", Now);

            ReleaseTracker after = New();
            await after.LoadAsync(path);

            Assert.Equal("202609261030", after.Latest);
            Assert.True(after.IsBehind("202609221211"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AnUnreadableMarkerAnnouncesNothingRatherThanThrowing()
    {
        string path = Path.Combine(Path.GetTempPath(), $"wwtb-release-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, "not json at all");
        try
        {
            ReleaseTracker tracker = New();
            await tracker.LoadAsync(path);
            Assert.Null(tracker.Latest);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
