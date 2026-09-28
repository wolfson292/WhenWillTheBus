// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core;
using WhenWillTheBus.Core.Api;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Prediction;
using WhenWillTheBus.Core.Storage;

namespace WhenWillTheBus.Server.Tests;

/// <summary>
/// Scans surviving a restart.
/// </summary>
/// <remarks>
/// THE SCHOOL-ARRIVAL ESTIMATE IS BUILT ENTIRELY FROM PAST DROP-OFF SCANS, and
/// the scan endpoint only ever returns TODAY — so everything it knows has to
/// come off disk. It did not: history was written only when an arrival was
/// learned, so a drop-off arriving after the day's last arrival was never
/// saved, and a restart put the file back.
///
/// Across a week of redeploys that left exactly one scan on disk. The estimate
/// then had nothing to work from at 08:01, which is the only moment it matters
/// — so the morning ride showed no progress bar at all, every day, while the
/// ride TO the stop worked perfectly.
/// </remarks>
public sealed class ScanPersistenceTests
{
    private static ScanEvent Scan(int day, int hour, int minute, ScanKind kind) =>
        new(new DateTimeOffset(2026, 9, day, hour, minute, 0, TimeSpan.Zero), "somewhere", kind, null);

    [Fact]
    public async Task AWeekOfScansSurvivesARestart()
    {
        string path = Path.Combine(Path.GetTempPath(), $"wwtb-history-{Guid.NewGuid():N}.json");
        try
        {
            List<ScanEvent> week = [];
            foreach (int day in (int[])[22, 23, 24, 25, 26])
            {
                week.Add(Scan(day, 12, 1, ScanKind.Pickup));
                week.Add(Scan(day, 13, 26, ScanKind.Dropoff));
            }

            await HistoryStore.SaveAsync(
                path, [new StoredRider { ChildId = 42, Arrivals = [], Scans = week }]);

            IReadOnlyList<StoredRider> back = await HistoryStore.LoadAsync(path);

            Assert.Equal(10, Assert.Single(back).Scans.Count);
            Assert.Equal(5, back[0].Scans.Count(scan => scan.Kind == ScanKind.Dropoff));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The one that decides whether a bar appears: with the week on disk, a
    /// morning has a target BEFORE the day's own drop-off has happened.
    /// </summary>
    [Fact]
    public void WithAWeekOnDiskTheMorningRideHasATargetFromTheStart()
    {
        LocalClock clock = new(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));

        List<ScanEvent> week = [];
        foreach (int day in (int[])[22, 23, 24, 25, 26])
        {
            week.Add(Scan(day, 12, 1, ScanKind.Pickup));
            week.Add(Scan(day, 13, 26, ScanKind.Dropoff));
        }

        // Today: she is ON the bus, and today's drop-off has NOT happened yet.
        week.Add(Scan(28, 12, 1, ScanKind.Pickup));

        Student student = new()
        {
            ChildId = 42,
            Name = "Rider",
            AmScheduled = new TimeOnly(7, 56),
            Scans = week,
        };

        // 08:30 local, mid-ride.
        DateTimeOffset now = new(2026, 9, 28, 12, 30, 0, TimeSpan.Zero);
        SchoolArrival? school = SchoolArrivalPredictor.Predict(student, now, clock);

        Assert.NotNull(school);
        Assert.Equal(5, school.Samples);
        Assert.Equal(new DateOnly(2026, 9, 28), clock.DateOf(school.Arrival));
    }

    /// <summary>
    /// And the fault as it actually presented: with only today's pickup known,
    /// there is nothing to predict from and therefore no bar.
    /// </summary>
    [Fact]
    public void WithNothingOnDiskThereIsNoTargetAndThatIsWhyTheBarWasMissing()
    {
        LocalClock clock = new(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));

        Student student = new()
        {
            ChildId = 42,
            Name = "Rider",
            AmScheduled = new TimeOnly(7, 56),
            Scans = [Scan(28, 12, 1, ScanKind.Pickup)],
        };

        Assert.Null(SchoolArrivalPredictor.Predict(
            student, new DateTimeOffset(2026, 9, 28, 12, 30, 0, TimeSpan.Zero), clock));
    }
}
