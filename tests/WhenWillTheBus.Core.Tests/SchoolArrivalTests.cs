// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core.Api;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Prediction;

namespace WhenWillTheBus.Core.Tests;

public sealed class SchoolArrivalTests
{
    private static ScanEvent Scan(int year, int month, int day, int hour, int minute, ScanKind kind) =>
        new(EngineFixtures.LocalAt(year, month, day, hour, minute), "somewhere", kind, null);

    private static Student WithScans(params ScanEvent[] scans) =>
        EngineFixtures.MorningOnly with { Scans = scans };

    [Fact]
    public void WithNoDropoffs_ThereIsNothingToLearnFrom() =>
        Assert.Null(SchoolArrivalPredictor.Predict(
            WithScans(Scan(2026, 9, 15, 7, 56, ScanKind.Pickup)),
            EngineFixtures.LocalAt(2026, 9, 17, 6, 0),
            EngineFixtures.Clock));

    [Fact]
    public void LearnsTheMedianDropoffTime()
    {
        Student student = WithScans(
            Scan(2026, 9, 15, 8, 20, ScanKind.Dropoff),
            Scan(2026, 9, 16, 8, 24, ScanKind.Dropoff),
            Scan(2026, 9, 17, 8, 22, ScanKind.Dropoff));

        SchoolArrival? school = SchoolArrivalPredictor.Predict(
            student, EngineFixtures.LocalAt(2026, 9, 18, 6, 0), EngineFixtures.Clock);

        Assert.NotNull(school);
        Assert.Equal(new TimeOnly(8, 22), TimeOnly.FromDateTime(EngineFixtures.Clock.ToLocal(school.Arrival)));
        Assert.Equal(3, school.Samples);
    }

    /// <summary>
    /// Pickup to drop-off on the SAME day, which is what lets a bar fill across
    /// the ride rather than only counting from boarding.
    /// </summary>
    [Fact]
    public void MeasuresTheTypicalRideFromPickupToDropoff()
    {
        Student student = WithScans(
            Scan(2026, 9, 15, 7, 56, ScanKind.Pickup),
            Scan(2026, 9, 15, 8, 20, ScanKind.Dropoff),
            Scan(2026, 9, 16, 7, 58, ScanKind.Pickup),
            Scan(2026, 9, 16, 8, 24, ScanKind.Dropoff));

        SchoolArrival? school = SchoolArrivalPredictor.Predict(
            student, EngineFixtures.LocalAt(2026, 9, 18, 6, 0), EngineFixtures.Clock);

        Assert.NotNull(school);
        Assert.Equal(25, school.RideMinutes);
    }

    /// <summary>
    /// A time already past stays on TODAY. This used to roll forward a day so a
    /// progress bar could not sit full, which moved the target off the only day
    /// the aboard stage will accept — so a ride running a minute late lost its
    /// stage entirely and the app fell back to the afternoon pickup.
    /// </summary>
    [Fact]
    public void ATimeAlreadyPastIsStillTodays()
    {
        Student student = WithScans(
            Scan(2026, 9, 15, 8, 20, ScanKind.Dropoff),
            Scan(2026, 9, 16, 8, 20, ScanKind.Dropoff));

        SchoolArrival? school = SchoolArrivalPredictor.Predict(
            student, EngineFixtures.LocalAt(2026, 9, 17, 10, 0), EngineFixtures.Clock);

        Assert.NotNull(school);
        Assert.Equal(new DateOnly(2026, 9, 17), EngineFixtures.Clock.DateOf(school.Arrival));
    }

    /// <summary>Afternoon scans say nothing about when the morning ride ends.</summary>
    [Fact]
    public void AfternoonScansAreIgnored()
    {
        Student student = WithScans(
            Scan(2026, 9, 15, 8, 20, ScanKind.Dropoff),
            Scan(2026, 9, 15, 17, 21, ScanKind.Dropoff),
            Scan(2026, 9, 16, 8, 20, ScanKind.Dropoff));

        SchoolArrival? school = SchoolArrivalPredictor.Predict(
            student, EngineFixtures.LocalAt(2026, 9, 17, 6, 0), EngineFixtures.Clock);

        Assert.NotNull(school);
        Assert.Equal(2, school.Samples);
        Assert.Equal(new TimeOnly(8, 20), TimeOnly.FromDateTime(EngineFixtures.Clock.ToLocal(school.Arrival)));
    }

    /// <summary>
    /// A morning stuck in traffic is a bad day, not the pattern — the same
    /// outlier rejection the stop arrivals use.
    /// </summary>
    [Fact]
    public void OneBadMorningDoesNotDragTheEstimate()
    {
        Student student = WithScans(
            Scan(2026, 9, 14, 8, 20, ScanKind.Dropoff),
            Scan(2026, 9, 15, 8, 21, ScanKind.Dropoff),
            Scan(2026, 9, 16, 8, 20, ScanKind.Dropoff),
            Scan(2026, 9, 17, 8, 22, ScanKind.Dropoff),
            Scan(2026, 9, 18, 9, 30, ScanKind.Dropoff));

        SchoolArrival? school = SchoolArrivalPredictor.Predict(
            student, EngineFixtures.LocalAt(2026, 9, 21, 6, 0), EngineFixtures.Clock);

        Assert.NotNull(school);
        Assert.Equal(4, school.Samples);
        Assert.InRange(TimeOnly.FromDateTime(EngineFixtures.Clock.ToLocal(school.Arrival)),
            new TimeOnly(8, 20), new TimeOnly(8, 21));
    }
}
