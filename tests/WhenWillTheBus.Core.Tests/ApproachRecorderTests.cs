// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core;
using WhenWillTheBus.Core.Prediction;

namespace WhenWillTheBus.Core.Tests;

public sealed class ApproachRecorderTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 14, 7, 0, 0, TimeSpan.FromHours(-4));
    private static readonly double[] Ladder = Tuning.AnchorLadderMiles;
    private const double Threshold = Tuning.ArrivalThresholdMiles;
    private static readonly GeoPoint Somewhere = new(40.73500, -74.02000);

    private static DateTimeOffset At(int seconds) => Start.AddSeconds(seconds);

    /// <summary>§3.4 and §11.6. The depot sits at EXACTLY 3.0 miles.</summary>
    /// <remarks>
    /// Without an inward margin, GPS jitter around the outer rung eventually
    /// satisfies "was outside, now inside" while the bus has not moved at all —
    /// and the recede hysteresis can never undo it, because a few metres of
    /// wobble never reaches 3.45.
    /// </remarks>
    [Fact]
    public void Jitter_AtExactlyTheRung_IsNotACrossing()
    {
        ApproachRecorder recorder = new();

        recorder.Sample(Ladder, At(0), 3.001, Threshold);
        recorder.Sample(Ladder, At(30), 2.999, Threshold);

        Assert.Empty(recorder.Crossings);
    }

    /// <summary>The margin must not be so wide that a real crossing is missed.</summary>
    [Fact]
    public void GenuineCrossing_IsRecorded()
    {
        ApproachRecorder recorder = new();

        recorder.Sample(Ladder, At(0), 3.2, Threshold);
        recorder.Sample(Ladder, At(30), 2.9, Threshold);

        Assert.Equal(At(30), recorder.Crossings[0]);
    }

    /// <summary>
    /// §3.4. On 11 Sep the bus sat at 2.7 mi, drifted back out to 3.8 serving
    /// other stops, then came in for real eight minutes later. Anchoring on the
    /// first touch would have been eight minutes wrong.
    /// </summary>
    [Fact]
    public void CrossingIsDiscarded_WhenTheBusGoesBackOutside()
    {
        ApproachRecorder recorder = new();

        recorder.Sample(Ladder, At(0), 3.2, Threshold);
        recorder.Sample(Ladder, At(30), 2.7, Threshold);
        Assert.True(recorder.Crossings.ContainsKey(0));

        recorder.Sample(Ladder, At(60), 3.8, Threshold);

        Assert.Empty(recorder.Crossings);
        Assert.Equal(1, recorder.Recedes);
    }

    /// <summary>A few metres of wobble back over a rung is not a recede.</summary>
    [Fact]
    public void SmallWobbleBackOverARung_IsNotARecede()
    {
        ApproachRecorder recorder = new();

        recorder.Sample(Ladder, At(0), 3.2, Threshold);
        recorder.Sample(Ladder, At(30), 2.95, Threshold);
        recorder.Sample(Ladder, At(60), 3.05, Threshold);

        Assert.True(recorder.Crossings.ContainsKey(0));
        Assert.Equal(0, recorder.Recedes);
    }

    /// <summary>
    /// §3.4 and §11.7. On 14 Sep the bus was parked at exactly 3.0 mi from
    /// 06:58; the first reading after the window opened at 07:16 was read as
    /// "just crossed the 3-mile rung", the typical 9.5-minute leg was hung off
    /// it, 07:25 was predicted for a bus that came at 08:01, and the five-minute
    /// warning went out at 07:20.
    /// </summary>
    [Fact]
    public void RungTheBusWasAlreadyInside_GetsNoCrossing()
    {
        ApproachRecorder recorder = new();

        // Watching begins with the bus already well inside the 3-mile rung.
        recorder.Sample(Ladder, At(0), 2.5, Threshold);
        recorder.Sample(Ladder, At(30), 2.4, Threshold);

        Assert.DoesNotContain(0, recorder.Crossings.Keys);

        // The estimate falls through to the next rung it is genuinely watched
        // crossing, which still carries real information.
        recorder.Sample(Ladder, At(60), 1.9, Threshold);

        Assert.Equal(At(60), recorder.Crossings[1]);
        Assert.DoesNotContain(0, recorder.Crossings.Keys);
    }

    /// <summary>
    /// §4 and §11.3. Sixty identical points across a half-hour freeze made every
    /// later journey through that spot match a block of ages thirty minutes
    /// wide, which the ambiguity guard then refused outright. The contamination
    /// and the guard against it were both self-inflicted.
    /// </summary>
    [Fact]
    public void StaleReading_NeverEntersTheTrack()
    {
        ApproachRecorder recorder = new();

        recorder.Sample(Ladder, At(0), 2.5, Threshold, Somewhere);
        recorder.Sample(Ladder, At(30), 2.5, Threshold, Somewhere, fresh: false);
        recorder.Sample(Ladder, At(60), 2.5, Threshold, Somewhere, fresh: false);

        Assert.Single(recorder.Track);
        Assert.Equal(2, recorder.Stale);
    }

    /// <summary>
    /// §3.4. A stale reading cannot time a crossing: when the feed thaws the bus
    /// has already moved, and stamping the rung at the thaw records a leg far
    /// shorter than the bus actually took.
    /// </summary>
    [Fact]
    public void StaleReading_CannotTimeACrossing()
    {
        ApproachRecorder recorder = new();

        recorder.Sample(Ladder, At(0), 3.2, Threshold);
        recorder.Sample(Ladder, At(30), 3.2, Threshold, fresh: false);
        recorder.Sample(Ladder, At(60), 2.9, Threshold);

        Assert.Empty(recorder.Crossings);
    }

    /// <summary>The journey is over; the bus pulling away is not new information.</summary>
    [Fact]
    public void OnceArrived_LaterReadingsAreIgnored()
    {
        ApproachRecorder recorder = new();

        recorder.Sample(Ladder, At(0), 3.2, Threshold);
        recorder.Sample(Ladder, At(30), 0.2, Threshold, Somewhere);
        Assert.True(recorder.Arrived);

        recorder.Sample(Ladder, At(60), 3.9, Threshold, Somewhere);

        Assert.Equal(0, recorder.Recedes);
        Assert.Single(recorder.Track);
    }

    [Fact]
    public void Finish_MeasuresEachLegFromItsCrossing()
    {
        ApproachRecorder recorder = new();

        recorder.Sample(Ladder, At(0), 3.2, Threshold);
        recorder.Sample(Ladder, At(30), 2.9, Threshold);    // crosses 3.0
        recorder.Sample(Ladder, At(120), 1.9, Threshold);   // crosses 2.0
        DateTimeOffset arrival = At(600);

        (IReadOnlyDictionary<int, int> legs, _, _, _) = recorder.Finish(arrival);

        Assert.Equal(570, legs[0]);
        Assert.Equal(480, legs[1]);
        Assert.DoesNotContain(2, legs.Keys);
    }

    /// <summary>
    /// §4. Trim to the arrival FIRST, then take the cap. Capping first counts
    /// readings from after the bus had been and gone against the budget, and
    /// throws away the approach itself — the part the whole estimate hangs on.
    /// </summary>
    [Fact]
    public void Finish_TrimsToTheArrival_BeforeApplyingTheCap()
    {
        ApproachRecorder recorder = new();
        DateTimeOffset arrival = At(300);

        for (int step = 0; step <= 20; step++)
        {
            // Sampled either side of the arrival. The recorder itself stops at
            // the stop, so feed it directly to exercise the trim.
            recorder.Sample(Ladder, At(step * 30), 2.0, Threshold, Somewhere);
        }

        (_, IReadOnlyList<TrackPoint> track, _, _) = recorder.Finish(arrival);

        Assert.All(track, point => Assert.True(point.SecondsBeforeArrival >= 0));
        Assert.Equal(11, track.Count);
    }

    /// <summary>When the cap bites, the samples NEAREST the arrival are kept.</summary>
    [Fact]
    public void Finish_KeepsTheSamplesNearestTheArrival_WhenTheCapBites()
    {
        ApproachRecorder recorder = new();
        int samples = Tuning.TrackSampleLimit + 50;

        for (int step = 0; step < samples; step++)
        {
            recorder.Sample(Ladder, At(step * 30), 2.0, Threshold, Somewhere);
        }

        DateTimeOffset arrival = At(samples * 30);
        (_, IReadOnlyList<TrackPoint> track, _, _) = recorder.Finish(arrival);

        Assert.Equal(Tuning.TrackSampleLimit, track.Count);

        // The retained window ends at the arrival, so the youngest sample is the
        // last one recorded, not the first.
        Assert.Equal(30, track[^1].SecondsBeforeArrival);
    }

    [Fact]
    public void Finish_RoundsCoordinatesToAboutAMetre()
    {
        ApproachRecorder recorder = new();
        recorder.Sample(Ladder, At(0), 2.0, Threshold, new GeoPoint(40.7350123456, -74.0200987654));

        (_, IReadOnlyList<TrackPoint> track, _, _) = recorder.Finish(At(60));

        Assert.Equal(40.73501, track[0].Latitude, precision: 9);
        Assert.Equal(-74.02010, track[0].Longitude, precision: 9);
    }
}
