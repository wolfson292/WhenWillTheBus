// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core.Model;

namespace WhenWillTheBus.Core.Prediction;

/// <summary>
/// Accumulates one run's approach as the readings come in: which rungs the bus
/// was watched crossing, and where it physically went.
/// </summary>
/// <remarks>
/// Shared by the live path and any replay of recorded history, so that a
/// journey learned from history and one watched as it happened produce the same
/// record. They drifted apart once before, and the replay quietly learned less.
/// </remarks>
public sealed class ApproachRecorder
{
    private readonly Dictionary<int, DateTimeOffset> _crossings = [];
    private readonly List<(DateTimeOffset When, double Latitude, double Longitude)> _track = [];

    /// <summary>Rung index to the instant the bus was watched crossing it inward.</summary>
    public IReadOnlyDictionary<int, DateTimeOffset> Crossings => _crossings;

    /// <summary>Where the bus has been. Never contains a stale fix.</summary>
    public IReadOnlyList<(DateTimeOffset When, double Latitude, double Longitude)> Track => _track;

    /// <summary>How often a crossing was taken back because the bus went out again.</summary>
    public int Recedes { get; private set; }

    /// <summary>How many readings arrived with a fix that was not current.</summary>
    public int Stale { get; private set; }

    /// <summary>
    /// Set once the bus has actually reached the stop. Everything after that is
    /// the bus leaving again, and must not be read as the approach coming apart.
    /// </summary>
    public bool Arrived { get; private set; }

    /// <summary>
    /// The distance from the previous reading. A rung is crossed by moving from
    /// outside it to inside it, which cannot be judged from one reading alone.
    /// </summary>
    public double? Previous { get; private set; }

    /// <summary>Fold one reading into the approach.</summary>
    /// <param name="distanceMiles">Drives the rungs and decides arrival.</param>
    /// <param name="position">What the route match reads back later.</param>
    /// <param name="fresh">Whether the GPS fix behind this reading is current.</param>
    public void Sample(
        IReadOnlyList<double> ladderMiles,
        DateTimeOffset when,
        double distanceMiles,
        double arrivalThresholdMiles,
        GeoPoint? position = null,
        bool fresh = true)
    {
        if (Arrived)
        {
            // The journey is over; the bus pulling away is not new information.
            return;
        }

        // A stale reading is the LAST known position repeated, not a new one.
        // Writing it anyway put sixty-odd identical points into a track,
        // spanning the half hour the feed was frozen, and any later journey
        // passing that spot then matched a block of ages thirty minutes wide and
        // was refused as ambiguous. The contamination and the guard against it
        // were both self-inflicted; this removes the first.
        if (position is not null && fresh)
        {
            _track.Add((when, position.Value.Latitude, position.Value.Longitude));
        }

        if (!fresh)
        {
            Stale++;

            // Nor can a frozen feed time a crossing. When it thaws the bus has
            // already moved, and stamping the rung at the thaw records a leg far
            // shorter than the bus actually took.
            Previous = null;
            return;
        }

        if (distanceMiles <= arrivalThresholdMiles)
        {
            Arrived = true;
        }

        for (int rung = 0; rung < ladderMiles.Count; rung++)
        {
            double threshold = ladderMiles[rung];

            // An inward margin, because the depot sits at EXACTLY the outer
            // rung. Without it, GPS jitter around 3.0 miles eventually satisfies
            // "was outside, now inside" while the bus has not moved at all, and
            // the recede hysteresis can never undo it: jitter of a few metres
            // never reaches 3.45.
            bool crossedInward = Previous is not null
                && Previous.Value > threshold * Tuning.CrossingMargin
                && distanceMiles <= threshold;

            if (crossedInward)
            {
                _crossings.TryAdd(rung, when);
            }
            else if (!Arrived
                && distanceMiles > threshold * Tuning.RecedeHysteresis
                && _crossings.ContainsKey(rung))
            {
                // It crossed, then went back out without ever reaching the stop:
                // that was not the final approach, so the crossing must not
                // anchor anything.
                _crossings.Remove(rung);
                Recedes++;
            }
        }

        // Deliberately last, and deliberately unconditional: A RUNG THE BUS WAS
        // ALREADY INSIDE WHEN WATCHING BEGAN GETS NO CROSSING AT ALL. On 14 Sep
        // the bus sat parked at exactly 3.0 miles from 06:58, and the first
        // reading after the window opened at 07:16 was read as "just crossed the
        // 3 mile rung". The typical nine-and-a-half minute leg was hung off
        // that, predicting 07:25 for a bus that came at 08:01, and the five
        // minute warning went out at 07:20. We never saw it cross, so we cannot
        // time it.
        Previous = distanceMiles;
    }

    /// <summary>Return the fields describing this approach, given when it ended.</summary>
    public (IReadOnlyDictionary<int, int> Legs, IReadOnlyList<TrackPoint> Track, int Recedes, int Stale) Finish(
        DateTimeOffset arrival)
    {
        Dictionary<int, int> legs = _crossings
            .Where(crossing => crossing.Value <= arrival)
            .ToDictionary(
                crossing => crossing.Key,
                crossing => (int)(arrival - crossing.Value).TotalSeconds);

        // Trim to the arrival FIRST, then take the cap. Capping first counts
        // readings from after the bus had already been and gone against the
        // budget, and throws away the approach itself — the part the whole
        // estimate hangs on.
        List<TrackPoint> track = _track
            .Where(point => point.When <= arrival)
            .Select(point => new TrackPoint(
                (int)(arrival - point.When).TotalSeconds,
                Math.Round(point.Latitude, Tuning.CoordinatePrecision),
                Math.Round(point.Longitude, Tuning.CoordinatePrecision)))
            .ToList();

        if (track.Count > Tuning.TrackSampleLimit)
        {
            track = track[^Tuning.TrackSampleLimit..];
        }

        return (legs, track, Recedes, Stale);
    }
}
