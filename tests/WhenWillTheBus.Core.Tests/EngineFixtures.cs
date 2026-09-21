// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core;
using WhenWillTheBus.Core.Api;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Prediction;

namespace WhenWillTheBus.Core.Tests;

/// <summary>A school day wired up so a whole prediction can be exercised.</summary>
internal static class EngineFixtures
{
    public const long ChildId = 4242;
    public const double Latitude = 40.74000;
    public const double WestEnd = -74.05000;
    public const double LegLongitude = 0.0045;

    /// <summary>Somewhere in the eastern United States, so the clock moves with the school.</summary>
    public static LocalClock Clock { get; } = new(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));

    public static Student MorningOnly { get; } = new()
    {
        ChildId = ChildId,
        Name = "Rider",
        BusNumber = "17",
        SchoolName = "Example Elementary",
        AmScheduled = new TimeOnly(8, 0),
    };

    public static DateTimeOffset LocalAt(int year, int month, int day, int hour, int minute) =>
        Clock.AtLocal(new DateOnly(year, month, day), new TimeOnly(hour, minute));

    /// <summary>
    /// A journey that ran due east towards the stop, sampled every thirty
    /// seconds, arriving at <paramref name="arrival"/>.
    /// </summary>
    public static RunArrival PastMorning(DateTimeOffset arrival, int samples = 41)
    {
        List<TrackPoint> track = [];
        for (int step = 0; step < samples; step++)
        {
            track.Add(new TrackPoint(
                (samples - 1 - step) * 30,
                Latitude,
                WestEnd + (step * LegLongitude)));
        }

        return new RunArrival
        {
            Run = Run.Am,
            Arrival = arrival,
            ClosestMiles = 0.05,
            Track = track,
            Legs = new Dictionary<int, int> { [0] = 570, [1] = 420, [2] = 240, [3] = 95 },
        };
    }

    /// <summary>
    /// The position a past journey occupied with <paramref name="remaining"/>
    /// seconds to go — interpolated, so a query may land BETWEEN two recorded
    /// fixes.
    /// </summary>
    /// <remarks>
    /// Deliberately fractional. Snapping every query onto a recorded sample asks
    /// only about points the recorder already holds, where any matcher that
    /// finds the right sample is exactly right — and it quietly made a test of
    /// the publish-hold unable to fail, because two different readings came back
    /// with the identical estimate. The bus spends nearly all its time between
    /// the fixes.
    /// </remarks>
    public static GeoPoint WhereItWasWith(double remaining, int samples = 41)
    {
        double step = (samples - 1) - (remaining / 30.0);
        return new GeoPoint(Latitude, WestEnd + (step * LegLongitude));
    }

    public static RiderInfo Reading(
        GeoPoint bus,
        double distanceMiles,
        BusStatusKind status = BusStatusKind.Current,
        int? ageMinutes = 0) => new()
        {
            Bus = bus,
            DistanceMiles = distanceMiles,
            Status = status,
            GpsAgeMinutes = ageMinutes,
            RawStatus = status == BusStatusKind.Current ? "current" : $"{ageMinutes} mins ago",
        };
}
