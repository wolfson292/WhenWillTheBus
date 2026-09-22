// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core.Api;
using WhenWillTheBus.Core.Model;

namespace WhenWillTheBus.Core.Prediction;

/// <summary>A predicted arrival at school on the morning run.</summary>
/// <param name="RideMinutes">
/// The typical ride, measured pickup to drop-off on the same day. What lets a
/// progress bar fill across the journey rather than only counting elapsed time.
/// </param>
public sealed record SchoolArrival(DateTimeOffset Arrival, int Samples, int? RideMinutes);

/// <summary>
/// When the morning ride reaches school.
/// </summary>
/// <remarks>
/// Learned from the drop-off scans the school itself records, which are already
/// in the scan history — the same median with the same outlier rejection used
/// for stop arrivals, so one morning stuck in traffic does not drag the estimate.
///
/// Without this the morning stage has no target to fill towards between boarding
/// and the classroom, which is most of what a parent wants to see once the rider
/// is aboard. It is a REFINEMENT, not a precondition: §5's rule 3 shows the ride
/// with or without one, because a history that has never recorded a morning
/// drop-off is exactly a history that cannot produce this.
/// </remarks>
public static class SchoolArrivalPredictor
{
    public static SchoolArrival? Predict(Student student, DateTimeOffset now, LocalClock clock)
    {
        List<int> arrivals = [];
        List<int> rides = [];
        Dictionary<DateOnly, DateTimeOffset> pickups = [];

        foreach (ScanEvent scan in student.Scans)
        {
            DateTime local = clock.ToLocal(scan.Timestamp);
            if (local.Hour >= Tuning.NoonHour)
            {
                continue;
            }

            DateOnly day = DateOnly.FromDateTime(local);

            if (scan.Kind == ScanKind.Pickup)
            {
                // The FIRST pickup of the day. A second scan in one morning is a
                // re-scan, not a second boarding.
                pickups.TryAdd(day, scan.Timestamp);
            }
            else
            {
                arrivals.Add((local.Hour * 60) + local.Minute);

                if (pickups.TryGetValue(day, out DateTimeOffset boarded))
                {
                    rides.Add((int)(scan.Timestamp - boarded).TotalMinutes);
                }
            }
        }

        if (arrivals.Count == 0)
        {
            return null;
        }

        arrivals.Sort();
        (IReadOnlyList<int> kept, _) = Statistics.RejectOutliers(arrivals);
        int middle = Statistics.Median(kept);

        // TODAY'S date, always -- this is the time the morning ride typically
        // ENDS, not a forecast of the next one.
        //
        // It used to roll to tomorrow once the time had passed, so that a
        // progress bar could not fill instantly and stay full. That cure was
        // worse: the only consumer is the aboard stage, which requires a target
        // dated today, so a ride running even a minute past the usual arrival
        // had its target moved out of reach and the stage collapsed to idle
        // mid-journey. Pinning a bar at 100% while a late bus finishes its run
        // is the honest reading; vanishing is not.
        DateTimeOffset moment = clock.AtLocal(clock.DateOf(now), new TimeOnly(middle / 60, middle % 60));

        int? ride = null;
        if (rides.Count > 0)
        {
            rides.Sort();
            ride = Statistics.Median(rides);
        }

        return new SchoolArrival(moment, kept.Count, ride);
    }
}
