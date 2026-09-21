// SPDX-License-Identifier: GPL-3.0-or-later

namespace WhenWillTheBus.Core.Model;

/// <summary>Which of the day's two trips past the rider's stop this is.</summary>
public enum Run
{
    /// <summary>The morning pickup.</summary>
    Am,

    /// <summary>The afternoon drop-off.</summary>
    Pm,
}

/// <summary>How an estimate was arrived at, loosest to tightest.</summary>
public enum PredictionBasis
{
    /// <summary>The published timetable, with nothing learned yet.</summary>
    Scheduled,

    /// <summary>The median clock time this run has actually arrived.</summary>
    Historical,

    /// <summary>An anchor-ladder rung crossed today, plus the typical leg from it.</summary>
    Approach,

    /// <summary>
    /// Where the bus is along the ROUTE, matched against where past journeys
    /// physically were. The only basis that survives a bus driving away from
    /// the stop, which it does constantly.
    /// </summary>
    Route,
}

/// <summary>Whether a prediction rests on observed arrivals or only the timetable.</summary>
public enum PredictionSource
{
    Scheduled,
    Learned,
}

/// <summary>The GPS freshness states <c>stsMsg</c> is collapsed to.</summary>
public enum BusStatusKind
{
    /// <summary>The API used a word we do not recognise. Not a crash.</summary>
    Unknown,

    /// <summary>The fix is live.</summary>
    Current,

    /// <summary>
    /// The last known position, repeated. NOT a new one — this distinction is
    /// responsible for two separate bugs in the reference implementation.
    /// </summary>
    Stale,

    /// <summary>Nothing is reporting at all.</summary>
    Inactive,
}

/// <summary>Whether a badge scan was the rider getting on or getting off.</summary>
public enum ScanKind
{
    Pickup,
    Dropoff,
}

/// <summary>The stages of one school run.</summary>
/// <remarks>
/// One enum, computed in one place. This started as ten independent branches
/// each working out for itself whether a journey was under way, and four
/// separate faults came out of the gaps between them: a progress bar that
/// filled for two hours after the rider reached school, a bar lurching between
/// 78% and 7%, a flickering title and colour, and a countdown that ran 61 hours
/// to the following Monday.
/// </remarks>
public enum JourneyStage
{
    Idle,
    ToStop,
    AtStop,
    ToSchool,
    AtSchool,
    FromSchool,
    Home,
}
