# Porting notes

<!-- SPDX-License-Identifier: GPL-3.0-or-later -->

What this port changed from the Python reference, and why. Where the handoff
document and the reference code disagreed, the code won.

## Deliberate changes

**Everything works in miles internally.** The reference carried parallel mile and
kilometre constants, and in two places compared a kilometre threshold against a
mile measurement — `ROUTE_MATCH_RADIUS_KM = 0.4` was checked against
`haversine_miles`, so a kilometre account got a 0.4 *mile* radius, and
`aboard.py` had the same shape. It never showed because the only install was in
miles. Converting once at the API boundary removes the whole class of bug;
`RiderInfo.From` reads `isDistKm` and converts there.

**History import remaps anchor rungs by distance, not by index.** Leg keys in the
export are indices into *that bundle's* ladder. If the ladder ever differs, a
three-mile leg is silently relabelled as a half-mile one and every anchored
estimate is wrong by the difference. Matching is nearest-rung within 5% relative
tolerance, which also absorbs the kilometre ladder's rounding — `[4.8, 3.2, 1.6,
0.8]` km is `[2.98, 1.99, 0.99, 0.50]` miles, plainly the same ladder and not
within any sensible absolute tolerance.

**The worker backs off outside run windows.** The reference polled every 30s
forever because Home Assistant was already running. This is somebody else's
service, running for schoolchildren, so outside every approach window there is
nothing a reading could change and the worker drops to 5-minute polling.

**The content state is shared.** `BusActivityState` lives in Core and both the
phone and the worker serialise through it. They start and update the same card,
and a divergence presents as an activity that silently ignores every push.

**Instants cross the wire as epoch seconds.** Swift's `.iso8601` decoding
strategy rejects fractional seconds, which .NET writes by default. Numbers cannot
be got wrong.

**Zero-zero coordinates are treated as "no position."** The API uses `0,0` for a
missing fix; taking it literally puts the marker in the Gulf of Guinea and hands
the route matcher a point no past journey can ever match.

**History is written atomically.** A torn write would cost roughly six weeks of
learned journeys, so the file is written beside the target and moved into place.

## Faithful ports, listed because they look like mistakes

- `Statistics.Median` averages an even pair. Taking the upper value biased every
  two-sample route estimate *late*, which is the direction that strands a rider.
- Arrival is the **first** reading inside the threshold, not the closest.
- `ApproachRecorder.Previous` is assigned **last and unconditionally**, so a rung
  the bus was already inside when watching began gets no crossing at all.
- The heading filter drops only samples *known* to be going the other way. A
  sample with no heading is a bus that was standing still there, which is a real
  place on the route.
- Distance leads the route match ranking; elapsed time is only a tie-break.
- The publish hold is the **band**, not a fixed tolerance, and the band is never
  dragged to cover a held value.

## Known-unreachable defensive code

`Statistics.RejectOutliers` refuses to discard every sample. With the MAD
formula this cannot happen — at least half the deviations are ≤ MAD ≤ threshold,
so the kept set is never empty. The guard is kept because it is cheap and the
formula could change, but **it has no test**, because no test could fail when it
is removed. Claiming coverage for it would be exactly the kind of test §11.10
warns about. The falsification harness deliberately does not list it.

## Not ported

- `backfill.py` / `async_restore_in_flight`: these replay Home Assistant's
  recorder to rebuild a run the integration restarted in the middle of. The
  equivalent here is the worker's persisted history plus the fact that it
  restarts in seconds, but a mid-run restart still loses that run's rung
  crossings. Worth revisiting if restarts turn out to be common.
- `device_tracker`, `diagnostics`, `sensor`: Home Assistant entity plumbing with
  no analogue.
