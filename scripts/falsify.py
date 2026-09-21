#!/usr/bin/env python3
"""Prove every guard in the prediction engine is actually load-bearing.

# SPDX-License-Identifier: GPL-3.0-or-later

Four tests shipped green in the reference implementation that passed
identically with the mechanism they named REMOVED -- including a backtest that
scored each journey against a history consisting of that same journey, where
every answer was right by construction.

So: for each mechanism, delete it from the source, run the test that claims to
cover it, and require that the test now FAILS. A test that still passes against
mutilated code is asserting something the surrounding code already did, and is
worth less than no test at all because it reads like cover.

Usage:  python3 scripts/falsify.py
"""
from __future__ import annotations

import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CORE = ROOT / "src" / "WhenWillTheBus.Core"


@dataclass(frozen=True)
class Mutation:
    """One mechanism, deleted, and the test that must notice."""

    name: str
    path: Path
    find: str
    replace: str
    test: str
    why: str


MUTATIONS = [
    Mutation(
        name="heading filter",
        path=CORE / "Prediction" / "RouteMatcher.cs",
        find="if (was is not null && Geo.TurnBetween(heading.Value, was.Value) > HeadingToleranceDegrees)",
        replace="if (false)",
        test="Heading_TellsTheTwoPassesOfACrossingApart",
        why="the two passes of a crossing collapse into whichever is nearer",
    ),
    Mutation(
        name="standing-still samples keep their claim",
        path=CORE / "Prediction" / "RouteMatcher.cs",
        find="if (was is not null && Geo.TurnBetween(heading.Value, was.Value) > HeadingToleranceDegrees)",
        replace="if (was is null || Geo.TurnBetween(heading.Value, was.Value) > HeadingToleranceDegrees)",
        test="SampleWithNoHeadingOfItsOwn_KeepsItsClaim",
        why="a bus that waited somewhere stops being able to locate today's",
    ),
    Mutation(
        name="ambiguity guard",
        path=CORE / "Prediction" / "RouteMatcher.cs",
        find="if (oldest - youngest > AmbiguousSpreadSeconds)",
        replace="if (false)",
        test="AmbiguousSpread_RefusesToAnswerRatherThanGuess",
        why="a parked bus answers confidently and is 31 minutes adrift",
    ),
    Mutation(
        name="point-to-segment projection",
        path=CORE / "Prediction" / "Geo.cs",
        find="along = Math.Clamp(along, 0.0, 1.0);",
        replace="along = along < 0.5 ? 0.0 : 1.0;",
        test="PointToSegment_IsExactAtMidpoints",
        why="the estimate quantises to the sample spacing and jumps between polls",
    ),
    Mutation(
        name="heading minimum movement",
        path=CORE / "Prediction" / "Geo.cs",
        find="public const double HeadingMinimumMiles = 0.02;",
        replace="public const double HeadingMinimumMiles = 0.0;",
        test="Heading_IsNullWhileTheBusIsOnlyJittering",
        why="GPS jitter at a stop is read as a bearing and points somewhere random",
    ),
    Mutation(
        name="median of an even pair",
        path=CORE / "Prediction" / "Statistics.cs",
        find=": (sortedValues[middle - 1] + sortedValues[middle]) / 2;",
        replace=": sortedValues[middle];",
        test="Median_OfTwo_IsTheMidpoint_NotTheLaterValue",
        why="every two-sample estimate is biased LATE, which strands the rider",
    ),
    Mutation(
        name="outlier floor",
        path=CORE / "Prediction" / "Statistics.cs",
        find="double threshold = Math.Max(OutlierMadMultiplier * Median(deviations), floor);",
        replace="double threshold = OutlierMadMultiplier * Median(deviations);",
        test="OutlierRejection_KeepsAnOrdinaryDelay_OnATightRun",
        why="a tight run rejects an ordinary five-minute delay as anomalous",
    ),
    Mutation(
        name="crossing margin",
        path=CORE / "Tuning.cs",
        find="public const double CrossingMargin = 1.02;",
        replace="public const double CrossingMargin = 1.0;",
        test="Jitter_AtExactlyTheRung_IsNotACrossing",
        why="the depot sits at exactly 3.0 mi and jitter eventually fakes a crossing",
    ),
    Mutation(
        name="recede hysteresis",
        path=CORE / "Prediction" / "ApproachRecorder.cs",
        find="&& distanceMiles > threshold * Tuning.RecedeHysteresis",
        replace="&& false",
        test="CrossingIsDiscarded_WhenTheBusGoesBackOutside",
        why="a bus weaving through other stops anchors on a pass that was not the approach",
    ),
    Mutation(
        name="already-inside rungs get no crossing",
        path=CORE / "Prediction" / "ApproachRecorder.cs",
        find=(
            "bool crossedInward = Previous is not null\n"
            "                && Previous.Value > threshold * Tuning.CrossingMargin\n"
        ),
        replace=(
            "bool crossedInward = (Previous is null"
            " || Previous.Value > threshold * Tuning.CrossingMargin)\n"
        ),
        test="RungTheBusWasAlreadyInside_GetsNoCrossing",
        why="a parked bus is read as having just crossed, predicting 07:25 for an 08:01 bus",
    ),
    Mutation(
        name="stale fixes never enter a track",
        path=CORE / "Prediction" / "ApproachRecorder.cs",
        find="if (position is not null && fresh)",
        replace="if (position is not null)",
        test="StaleReading_NeverEntersTheTrack",
        why="identical repeated points make every later journey through that spot unmatchable",
    ),
    Mutation(
        name="a frozen feed cannot time a crossing",
        path=CORE / "Prediction" / "ApproachRecorder.cs",
        find="            Previous = null;\n            return;",
        replace="            return;",
        test="StaleReading_CannotTimeACrossing",
        why="the rung is stamped at the thaw, recording a leg far shorter than reality",
    ),
    Mutation(
        name="estimate anchored to the fix, not the clock",
        path=CORE / "Prediction" / "PredictionEngine.cs",
        find=(
            "DateTimeOffset observed = info.GpsAgeMinutes is > 0\n"
            "            ? now - TimeSpan.FromMinutes(info.GpsAgeMinutes.Value)\n"
            "            : now;"
        ),
        replace="DateTimeOffset observed = now;",
        test="FrozenFeed_DoesNotWalkTheArrivalLater",
        why="a feed freeze walks the arrival one second later per second, stranding the rider",
    ),
    Mutation(
        name="an inactive feed is not a position",
        path=CORE / "Prediction" / "PredictionEngine.cs",
        find="if (info.Status == BusStatusKind.Inactive)",
        replace="if (false)",
        test="InactiveFeed_DoesNotCarryARouteEstimate",
        why="a bus nothing is reporting still answers confidently from its last fix",
    ),
    Mutation(
        name="already-arrived guard, shared by all three bases",
        path=CORE / "Prediction" / "PredictionEngine.cs",
        find=(
            "    public bool AlreadyArrived(long childId, Run run, DateOnly day)\n"
            "    {\n"
            "        RunKey key = new(childId, run, day);"
        ),
        replace=(
            "    public bool AlreadyArrived(long childId, Run run, DateOnly day)\n"
            "    {\n"
            "        if (true) { return false; }\n"
            "        RunKey key = new(childId, run, day);"
        ),
        test="OnceTheBusHasArrived_TodaysRunIsNoLongerTheNextOne",
        why="the estimate keeps predicting a bus that has already been and gone",
    ),
    Mutation(
        name="steadying the published answer",
        path=CORE / "Prediction" / "PredictionEngine.cs",
        find="        double drift = Math.Abs((prediction.Arrival - held).TotalSeconds);",
        replace=(
            "        if (true) { return false; }\n"
            "        double drift = Math.Abs((prediction.Arrival - held).TotalSeconds);"
        ),
        test="PublishedArrival_IsHeldWhileItStaysInsideTheBand",
        why="every poll-to-poll wobble is republished: 25 pushes in 20 minutes",
    ),
    Mutation(
        name="looking far enough ahead to reach Monday",
        path=CORE / "Tuning.cs",
        find="public const int DaysAhead = 8;",
        replace="public const int DaysAhead = 2;",
        test="FridayEvening_PredictsMonday",
        why="a Friday evening cannot see past the weekend and predicts nothing at all",
    ),
    Mutation(
        name="route band floor",
        path=CORE / "Tuning.cs",
        find="public static readonly TimeSpan RouteBandFloor = TimeSpan.FromSeconds(30);",
        replace="public static readonly TimeSpan RouteBandFloor = TimeSpan.Zero;",
        test="RouteBand_IsNeverNarrowerThanItsFloor",
        why="two journeys that happen to agree are reported as certainty",
    ),
    Mutation(
        name="legs remapped by distance, not by index",
        path=ROOT / "src" / "WhenWillTheBus.Core" / "Storage" / "HistoryImport.cs",
        find="(int ours, double gap) = NearestRung(rungMiles);",
        replace="(int ours, double gap) = (exportedIndex, 0.0);",
        test="Parse_RemapsLegsByDistance_NotByIndex",
        why="a three-mile leg is silently relabelled as a half-mile one",
    ),
    Mutation(
        name="target rounded to the displayed minute",
        path=CORE / "Notifications" / "PushPolicy.cs",
        find="if (Rounded(last.Target) != Rounded(journey.Target))",
        replace="if (last.Target != journey.Target)",
        test="TargetDriftingWithinTheSameDisplayedMinute_IsNotNews",
        why="sub-second drift republishes every poll: 25 pushes in 20 minutes",
    ),
    Mutation(
        name="only a stage change may interrupt",
        path=CORE / "Notifications" / "PushPolicy.cs",
        find="            return PushDecision.TimeSensitive;",
        replace="            return PushDecision.Passive;",
        test="StageChange_IsTheOnlyThingWorthABuzz",
        why="the one update a parent needs to feel arrives silently",
    ),
    Mutation(
        name="periodic backstop",
        path=CORE / "Notifications" / "PushPolicy.cs",
        find="return now - last.PushedAt >= (backstop ?? Backstop) ? PushDecision.Passive : PushDecision.None;",
        replace="return PushDecision.None;",
        test="Backstop_EventuallyRefreshesAQuietCard",
        why="a card can sit stale indefinitely with nothing to refresh it",
    ),
]


def run_suite() -> bool:
    """Return whether the whole suite passes, unmutated."""
    result = subprocess.run(
        ["dotnet", "test", str(ROOT / "tests" / "WhenWillTheBus.Core.Tests"), "--nologo", "-v", "q"],
        capture_output=True,
        text=True,
        cwd=ROOT,
    )
    return result.returncode == 0


def run_test(test: str) -> bool:
    """Return whether the named test PASSES."""
    result = subprocess.run(
        ["dotnet", "test", str(ROOT / "tests" / "WhenWillTheBus.Core.Tests"),
         "--nologo", "-v", "q", "--filter", test],
        capture_output=True,
        text=True,
        cwd=ROOT,
    )
    if "No test matches" in result.stdout or "Failed:     0, Passed:     0" in result.stdout:
        raise SystemExit(f"  !! no test named {test!r} -- the mutation list is stale")
    return result.returncode == 0


def main() -> int:
    print("Falsifying the prediction engine: each guard removed, its test must fail.\n")

    if not run_suite():
        print("Baseline is already red. Fix the suite before falsifying it.")
        return 1

    survivors: list[Mutation] = []
    for mutation in MUTATIONS:
        original = mutation.path.read_text()
        if mutation.find not in original:
            print(f"  !! STALE   {mutation.name}: anchor not found in {mutation.path.name}")
            survivors.append(mutation)
            continue

        mutation.path.write_text(original.replace(mutation.find, mutation.replace, 1))
        try:
            still_passes = run_test(mutation.test)
        finally:
            mutation.path.write_text(original)

        if still_passes:
            print(f"  !! USELESS {mutation.name}")
            print(f"     {mutation.test} passes with the mechanism removed.")
            survivors.append(mutation)
        else:
            print(f"  ok         {mutation.name}")
            print(f"             without it: {mutation.why}")

    print()
    if survivors:
        print(f"{len(survivors)} of {len(MUTATIONS)} mechanisms are not actually covered.")
        return 1

    print(f"All {len(MUTATIONS)} mechanisms are load-bearing and covered.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
