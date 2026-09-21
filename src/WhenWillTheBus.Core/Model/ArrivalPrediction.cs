// SPDX-License-Identifier: GPL-3.0-or-later

namespace WhenWillTheBus.Core.Model;

/// <summary>A predicted arrival of the bus at a rider's stop.</summary>
public sealed record ArrivalPrediction
{
    public required Run Run { get; init; }

    /// <summary>The published arrival — held still unless the estimate really moved.</summary>
    public required DateTimeOffset Arrival { get; init; }

    public required PredictionSource Source { get; init; }

    public required PredictionBasis Basis { get; init; }

    /// <summary>How many past arrivals the clock median rests on.</summary>
    public int Samples { get; init; }

    /// <summary>The observed spread of those arrivals, in minutes.</summary>
    public int? SpreadMinutes { get; init; }

    /// <summary>How many days were held out of the calculation as anomalies.</summary>
    public int Outliers { get; init; }

    /// <summary>The published timetable for this run, whatever it is worth.</summary>
    public TimeOnly? Scheduled { get; init; }

    /// <summary>Which rung the estimate hangs on, while it hangs on one.</summary>
    public double? AnchoredAtMiles { get; init; }

    /// <summary>How many past journeys backed that rung's typical leg.</summary>
    public int? AnchorSamples { get; init; }

    /// <summary>How many past journeys were near enough to speak to today's position.</summary>
    public int? RouteSamples { get; init; }

    /// <summary>
    /// The earliest and latest this arrival has been, judged the same way as the
    /// estimate itself. NOT a statistical interval — the observed range, which
    /// is the honest thing to show off a handful of journeys. It narrows on its
    /// own as the bus closes in, because what is left to vary is the part of the
    /// journey still to run.
    /// </summary>
    public DateTimeOffset? Earliest { get; init; }

    public DateTimeOffset? Latest { get; init; }

    /// <summary>
    /// Where the run's window was centred — the learned arrival once there is
    /// one, otherwise the timetable. Exposed because a window centred on a
    /// timetable twenty minutes out is the failure it hides behind.
    /// </summary>
    public TimeOnly? Centre { get; init; }

    /// <summary>How wide the band is. Floored, because two agreeing journeys are not certainty.</summary>
    public TimeSpan? Uncertainty => Earliest is null || Latest is null ? null : Latest.Value - Earliest.Value;
}
