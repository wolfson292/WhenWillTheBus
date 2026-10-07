// SPDX-License-Identifier: GPL-3.0-or-later

namespace WhenWillTheBus.Core;

/// <summary>
/// Every tuning constant, with what it cost to learn. All distances in MILES:
/// the API's kilometre accounts are converted once, at the boundary.
/// </summary>
public static class Tuning
{
    /// <summary>
    /// The API advertises a 15-second refresh. 30 keeps the marker useful at
    /// half the request rate against somebody else's service.
    /// </summary>
    public static readonly TimeSpan BusPollInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Roster and scan history change a handful of times a day. Known to be too
    /// slow around afternoon loading — see the open work in the handoff.
    /// </summary>
    public static readonly TimeSpan ScanPollInterval = TimeSpan.FromSeconds(300);

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How close the bus must come for a pass to count as "it stopped here". A
    /// run where nobody boards can stay half a mile out, so a loose threshold
    /// would learn arrivals that never happened.
    /// </summary>
    public const double ArrivalThresholdMiles = 0.3;

    /// <summary>
    /// The bus passes the stop on unrelated routes at other times — observed
    /// touching the stop at 06:13 for an 07:56 pickup. Only arrivals within
    /// this far either side of the run's LEARNED centre are recognised.
    /// </summary>
    public static readonly TimeSpan RunWindow = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Watching has to start well before the arrival window opens, or the outer
    /// rungs are never seen being crossed.
    /// </summary>
    /// <remarks>
    /// The window used to be centred on the timetable, and the afternoon
    /// timetable here reads 17:48 against a real arrival around 17:20 — so the
    /// window opened at 17:18, by which time the bus had already crossed the 3,
    /// 2 and 1 mile rungs. Every one was discarded and the estimate fell back
    /// to the clock median all afternoon.
    /// </remarks>
    public static readonly TimeSpan ApproachLead = TimeSpan.FromMinutes(45);

    /// <summary>
    /// A ladder of anchors, not one. Each rung records how long the rest of the
    /// journey took from that distance, and the estimate re-anchors to the
    /// tightest rung the bus has been WATCHED crossing.
    /// </summary>
    /// <remarks>
    /// A single 1.0-mile anchor left everything above it running on the clock
    /// median: on 3 Sep the bus was 2.2 miles out and 6 minutes away while the
    /// estimate still said 15.
    /// </remarks>
    public static readonly double[] AnchorLadderMiles = [3.0, 2.0, 1.0, 0.5];

    /// <summary>
    /// How far OUTSIDE a rung the previous reading must have been for the next
    /// one to count as crossing it inward.
    /// </summary>
    /// <remarks>
    /// The bus parks at exactly 3.0 miles, which is the outer rung, so a bare
    /// "was above, now at or below" is satisfied sooner or later by GPS jitter
    /// while the bus stands still — and the recede hysteresis cannot undo it,
    /// because a few metres of wobble never reaches 3.45. Two per cent of three
    /// miles is about 320 feet: far more than a fix wanders, far less than a
    /// bus travels between polls.
    /// </remarks>
    public const double CrossingMargin = 1.02;

    /// <summary>
    /// A crossing is discarded once the bus is back outside that rung by this
    /// factor, which is loose enough to ignore GPS jitter.
    /// </summary>
    /// <remarks>
    /// On 11 Sep the bus sat at 2.7 miles at 17:05, drifted back out to 3.8
    /// serving other stops, then came in for real at 17:13 — anchoring on the
    /// first touch would have been eight minutes wrong.
    /// </remarks>
    public const double RecedeHysteresis = 1.15;

    /// <summary>
    /// How close the bus must come before the morning bus counts as having SET
    /// OFF, which is when the worker starts the card on the phone by itself.
    /// </summary>
    /// <remarks>
    /// The morning bus waits between 3.03 and 3.12 miles out and leaves 8 to 14
    /// minutes before it reaches the stop: on 7 Oct it read 3.08, 3.07, 2.99,
    /// then 2.68 a minute later, and arrived eight minutes after that. Inside
    /// the outer rung by more than a fix wanders while the bus stands still,
    /// and crossed within a poll of it actually moving.
    ///
    /// Cutting this close matters because nobody opens the app on a school
    /// morning. On 7 Oct the app was opened by hand at 07:48, its card never
    /// reached the worker, and the only estimate the card ever had was one
    /// made before the bus had moved.
    /// </remarks>
    public const double DepartedMiles = 2.9;

    /// <summary>How long before the bus is due the "time to go" alert is sent.</summary>
    public static readonly TimeSpan FinalAlertLead = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How far a new estimate must move before the published one follows it,
    /// used ONLY when there is no band to judge against.
    /// </summary>
    /// <remarks>
    /// The route match is accurate and noisy at once: measured on 16 Sep the
    /// target swung across twelve minutes (17:18–17:30), changing every 30–60
    /// seconds, while the bus arrived within a minute of where the median had
    /// sat the whole time. Publishing every wobble made the display jitter and
    /// drove 25 notification pushes in 20 minutes.
    /// </remarks>
    public static readonly TimeSpan ArrivalHysteresis = TimeSpan.FromSeconds(120);

    /// <summary>Below this, nothing is worth republishing however tight the band gets.</summary>
    public static readonly TimeSpan ArrivalHysteresisFloor = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The narrowest a route-matched band may claim to be. On 17 Sep a
    /// two-sample match reported earliest and latest as the same instant —
    /// uncertainty of zero, from a sample of two.
    /// </summary>
    public static readonly TimeSpan RouteBandFloor = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Capped by count: about 100 minutes at a 30-second poll, comfortably more
    /// than the ~70-minute afternoon run. When the cap bites, the samples
    /// NEAREST the arrival are kept — that is the part the estimate hangs on.
    /// </summary>
    public const int TrackSampleLimit = 200;

    /// <summary>A little over a metre. Seven decimals would triple the store to record GPS noise.</summary>
    public const int CoordinatePrecision = 5;

    /// <summary>
    /// Kept per run, not shared. A combined cap lets the two daily runs compete,
    /// so a stretch of missed afternoons quietly evicts mornings that were still
    /// worth learning from. 30 each is roughly six school weeks.
    /// </summary>
    public const int ArrivalHistoryLimit = 30;

    /// <summary>Half a mile: far enough that a walk across school grounds does not count.</summary>
    public const double AboardMovedMiles = 0.5;

    /// <summary>
    /// A school bus is about twelve metres long and a GPS fix is good to eight,
    /// so a quarter mile is generous. It is not trying to prove which seat.
    /// </summary>
    public const double AboardTogetherMiles = 0.25;

    /// <summary>How long "arrived" is worth saying before the day goes quiet again.</summary>
    public static readonly TimeSpan ArrivedDwell = TimeSpan.FromMinutes(3);

    /// <summary>
    /// How far past a learned end-of-ride the "aboard" stage may still run.
    /// </summary>
    /// <remarks>
    /// The scan that ends a ride is the school's, not ours, and it arrives when
    /// it arrives -- so a ride running late must not be read as a ride that
    /// ended. It cannot be unbounded either: an early fault left a morning
    /// progress bar filling for two hours after the rider was in class.
    /// </remarks>
    public static readonly TimeSpan RideOverrun = TimeSpan.FromMinutes(45);

    /// <summary>
    /// How long an aboard stage may run when NOTHING has been learned about the
    /// ride yet, and there is therefore no target to overrun.
    /// </summary>
    /// <remarks>
    /// Generous, because it is bounding the one case with no evidence at all:
    /// the observed morning ride here is 83 minutes, and a cap that cut it off
    /// mid-journey would be the very fault this is here to prevent.
    /// </remarks>
    public static readonly TimeSpan UnlearnedRide = TimeSpan.FromHours(2);

    /// <summary>
    /// The API reports fix age in whole minutes, so a fix instant computed
    /// straight from the clock wobbles across minute boundaries. A new instant
    /// must beat the standing one by more than this to replace it.
    /// </summary>
    public static readonly TimeSpan GpsFixHysteresis = TimeSpan.FromSeconds(90);

    /// <summary>
    /// How far ahead to look for the next run. A Friday evening has to reach
    /// Monday, and a week covers any holiday the history has taught us about.
    /// </summary>
    public const int DaysAhead = 8;

    /// <summary>Monday to Friday, before anything has been learned.</summary>
    public const int SchoolWeek = 5;

    /// <summary>Local hour dividing the morning run from the afternoon one.</summary>
    public const int NoonHour = 12;
}
