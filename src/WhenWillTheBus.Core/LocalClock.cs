// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core.Model;

namespace WhenWillTheBus.Core;

/// <summary>
/// The rider's local day. Everything about a school run — which run it is, when
/// a window opens, whether an arrival is "today" — is a question about local
/// wall-clock time, while every instant is stored as an absolute.
/// </summary>
/// <remarks>
/// Injected rather than read from the machine so that every stage transition
/// can be tested directly at any instant of any school day, including the two
/// days a year the clocks move.
/// </remarks>
public sealed class LocalClock(TimeZoneInfo zone, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public TimeZoneInfo Zone { get; } = zone;

    public DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>The local wall-clock reading of an absolute instant.</summary>
    public DateTime ToLocal(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Zone).DateTime;

    /// <summary>The local calendar date an instant falls on.</summary>
    public DateOnly DateOf(DateTimeOffset instant) => DateOnly.FromDateTime(ToLocal(instant));

    /// <summary>The instant at which a given wall-clock time occurs on a given local day.</summary>
    public DateTimeOffset AtLocal(DateOnly day, TimeOnly clock)
    {
        DateTime naive = day.ToDateTime(clock, DateTimeKind.Unspecified);
        return new DateTimeOffset(naive, Zone.GetUtcOffset(naive));
    }

    /// <summary>The instant at which a wall-clock time occurs on the same local day as a reference.</summary>
    public DateTimeOffset OnSameDay(DateTimeOffset reference, TimeOnly clock) => AtLocal(DateOf(reference), clock);

    /// <summary>Whether two instants fall on the same local day.</summary>
    public bool SameDay(DateTimeOffset? moment, DateTimeOffset now) =>
        moment is not null && DateOf(moment.Value) == DateOf(now);

    /// <summary>
    /// Which of the day's two runs a local instant belongs to, FROM THE CLOCK.
    /// </summary>
    /// <remarks>
    /// Deliberately not from whichever run is predicted next: those differ the
    /// moment the bus arrives, because the prediction rolls straight on to the
    /// afternoon. Reading the stage off the prediction announced "Home" while
    /// the rider was still standing at the kerb waiting to be let on for school.
    /// </remarks>
    public Run RunOf(DateTimeOffset moment) => ToLocal(moment).Hour < Tuning.NoonHour ? Run.Am : Run.Pm;

    /// <summary>
    /// A stable identifier for the journey an instant belongs to, e.g.
    /// "20260914-am". Used as the Live Activity identity, so a notification
    /// carrying it never updates yesterday's.
    /// </summary>
    /// <remarks>
    /// Derived from the clock rather than from the prediction's run attribute,
    /// which flips to the afternoon the instant the morning pickup passes and
    /// would rename a journey halfway through it.
    /// </remarks>
    public string JourneyId(DateTimeOffset moment) =>
        $"{ToLocal(moment):yyyyMMdd}-{(RunOf(moment) == Run.Am ? "am" : "pm")}";
}
