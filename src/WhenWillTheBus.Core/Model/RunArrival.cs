// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core.Prediction;

namespace WhenWillTheBus.Core.Model;

/// <summary>One observed arrival of the bus at a rider's stop.</summary>
public sealed record RunArrival
{
    /// <summary>Which of the day's two runs this was.</summary>
    public required Run Run { get; init; }

    /// <summary>
    /// The FIRST reading inside the arrival threshold, not the closest.
    /// </summary>
    /// <remarks>
    /// A bus dwelling at the stop gives two or three readings under the
    /// threshold, and "closest" lands on whichever poll happened to have the
    /// best fix — dating the arrival, and every leg measured back from it, by a
    /// poll of noise.
    /// </remarks>
    public required DateTimeOffset Arrival { get; init; }

    /// <summary>How near the bus actually got, in miles.</summary>
    public required double ClosestMiles { get; init; }

    /// <summary>
    /// A replacement vehicle keeps its own time. Recorded so the arrival stays
    /// visible, but held out of what the estimate learns from.
    /// </summary>
    public bool Substitute { get; init; }

    /// <summary>
    /// Seconds from crossing each anchor rung to reaching the stop, keyed by
    /// rung INDEX into the ladder. Empty where the bus was already inside every
    /// rung when the window opened.
    /// </summary>
    public IReadOnlyDictionary<int, int> Legs { get; init; } = new Dictionary<int, int>();

    /// <summary>
    /// Where the bus was, oldest first. Positions rather than distances,
    /// because a school run is a route rather than an approach.
    /// </summary>
    public IReadOnlyList<TrackPoint> Track { get; init; } = [];

    /// <summary>
    /// How often the bus crossed a rung and then fell back outside it. A run
    /// with several of these was weaving through nearby stops, and its legs are
    /// correspondingly less representative.
    /// </summary>
    public int Recedes { get; init; }

    /// <summary>
    /// Samples during the approach where the GPS fix was not current, so the
    /// distances behind them were extrapolated rather than observed.
    /// </summary>
    public int Stale { get; init; }

    /// <summary>When the rider boarded for this run, where a scan recorded it.</summary>
    public DateTimeOffset? Boarded { get; init; }

    /// <summary>Whether this arrival was replayed from history rather than watched live.</summary>
    public bool Replayed { get; init; }

    /// <summary>
    /// How wrong the estimate was five minutes before the bus actually arrived,
    /// in seconds. Positive means the estimate was LATE -- it said the bus would
    /// come after it did.
    /// </summary>
    /// <remarks>
    /// Five minutes because that is the horizon that matters: it is when a
    /// parent decides whether to walk out of the door. The reference
    /// implementation measured worst case 1.19 minutes out over this window, and
    /// there is no way to know whether this port matches without recording it.
    ///
    /// Null where nothing was predicted that close to the arrival -- a run that
    /// was never watched, or one replayed from history.
    /// </remarks>
    public int? ErrorAtFiveMinutes { get; init; }

    /// <summary>
    /// How wrong the LAST estimate before arrival was, in seconds. Positive
    /// means late.
    /// </summary>
    public int? ErrorAtArrival { get; init; }

    /// <summary>
    /// How much this record knows, for deciding which of two records of the
    /// same arrival to keep.
    /// </summary>
    /// <remarks>
    /// "Knows more" has to count the route track as well as the rungs. Ranking
    /// on rungs alone let an old record with four rungs and no positions beat a
    /// replayed one with the same four rungs and the whole route — so the
    /// positions the route match needs were discarded the moment they arrived,
    /// and would have been on every restart thereafter.
    /// </remarks>
    public (int Legs, int Track) Knows => (Legs.Count, Track.Count);
}
