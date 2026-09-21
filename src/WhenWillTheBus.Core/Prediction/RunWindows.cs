// SPDX-License-Identifier: GPL-3.0-or-later

namespace WhenWillTheBus.Core.Prediction;

/// <summary>
/// When a run is expected, when an arrival at the stop is believed genuine, and
/// when the approach is worth watching.
/// </summary>
public static class RunWindows
{
    /// <summary>
    /// The local instant a run is expected to arrive.
    /// </summary>
    /// <remarks>
    /// <paramref name="learned"/> is the median of arrivals actually observed,
    /// and it wins over the timetable whenever there is one. THE PUBLISHED
    /// TIMETABLE CAN BE BADLY WRONG: the afternoon one here reads 17:48 against
    /// a real arrival around 17:20. Everything downstream is positioned
    /// relative to this, so centring on the timetable opens every window after
    /// the bus has already gone.
    /// </remarks>
    public static DateTimeOffset? Centre(
        LocalClock clock,
        TimeOnly? scheduled,
        TimeOnly? learned,
        DateTimeOffset reference)
    {
        TimeOnly? centre = learned ?? scheduled;
        return centre is null ? null : clock.OnSameDay(reference, centre.Value);
    }

    /// <summary>The window in which an arrival at the stop is believed to be this run's.</summary>
    /// <remarks>
    /// The bus passes the stop on unrelated routes at other times — observed
    /// touching the stop at 06:13 for an 07:56 pickup. This is what keeps the
    /// decoy passes out.
    /// </remarks>
    public static (DateTimeOffset Start, DateTimeOffset End)? Arrival(
        LocalClock clock,
        TimeOnly? scheduled,
        TimeOnly? learned,
        DateTimeOffset reference)
    {
        DateTimeOffset? centre = Centre(clock, scheduled, learned, reference);
        return centre is null ? null : (centre.Value - Tuning.RunWindow, centre.Value + Tuning.RunWindow);
    }

    /// <summary>The window in which the run's approach is worth recording.</summary>
    /// <remarks>
    /// Opens earlier than the arrival window so the outer rungs are seen at all,
    /// and closes with it: a bus still out at three miles half an hour after it
    /// should have arrived is on some other errand.
    /// </remarks>
    public static (DateTimeOffset Start, DateTimeOffset End)? Approach(
        LocalClock clock,
        TimeOnly? scheduled,
        TimeOnly? learned,
        DateTimeOffset reference)
    {
        DateTimeOffset? centre = Centre(clock, scheduled, learned, reference);
        return centre is null ? null : (centre.Value - Tuning.ApproachLead, centre.Value + Tuning.RunWindow);
    }

    /// <summary>Whether an instant falls inside a window.</summary>
    public static bool IsOpen((DateTimeOffset Start, DateTimeOffset End)? window, DateTimeOffset now) =>
        window is not null && window.Value.Start <= now && now <= window.Value.End;
}
