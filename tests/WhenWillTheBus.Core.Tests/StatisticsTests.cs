// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core.Prediction;

namespace WhenWillTheBus.Core.Tests;

public sealed class StatisticsTests
{
    /// <summary>
    /// §3.6 and §11.9. Taking the upper of two is not a median, it is the later
    /// sample — and with a two-sample minimum on route matching that was the
    /// common case, biasing every route estimate LATE by however far the two
    /// journeys disagreed. Late is the direction that strands a child.
    /// </summary>
    [Fact]
    public void Median_OfTwo_IsTheMidpoint_NotTheLaterValue() =>
        Assert.Equal(15, Statistics.Median([10, 20]));

    [Fact]
    public void Median_OfOdd_IsTheMiddleValue() => Assert.Equal(20, Statistics.Median([10, 20, 90]));

    [Fact]
    public void Median_OfOne_IsThatValue() => Assert.Equal(42, Statistics.Median([42]));

    [Fact]
    public void Median_RefusesAnEmptyList() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Statistics.Median([]));

    /// <summary>
    /// §3.6. Two arrivals cannot tell you which of them is the anomaly, so
    /// below three samples everything is kept.
    /// </summary>
    [Fact]
    public void OutlierRejection_KeepsEverythingBelowThreeSamples()
    {
        (IReadOnlyList<int> kept, int excluded) = Statistics.RejectOutliers([480, 600]);

        Assert.Equal(2, kept.Count);
        Assert.Equal(0, excluded);
    }

    /// <summary>
    /// §3.6. The floor stops the MAD cut being over-eager: a run clustered
    /// inside two minutes would otherwise reject an ordinary five-minute delay
    /// as anomalous, and a genuinely late bus is exactly what a parent needs
    /// the estimate to notice.
    /// </summary>
    [Fact]
    public void OutlierRejection_KeepsAnOrdinaryDelay_OnATightRun()
    {
        // Arrivals in minutes-of-day: five clustered inside two minutes, then an
        // ordinary six-minute delay. MAD here is 1, so 4.45 x MAD would cut it.
        int[] arrivals = [480, 480, 481, 481, 482, 487];

        (IReadOnlyList<int> kept, int excluded) = Statistics.RejectOutliers(arrivals);

        Assert.Contains(487, kept);
        Assert.Equal(0, excluded);
    }

    /// <summary>A genuinely anomalous day is still cut.</summary>
    [Fact]
    public void OutlierRejection_CutsABreakdown()
    {
        // The same tight run, plus a day the bus arrived fifty minutes late.
        int[] arrivals = [480, 480, 481, 481, 482, 530];

        (IReadOnlyList<int> kept, int excluded) = Statistics.RejectOutliers(arrivals);

        Assert.DoesNotContain(530, kept);
        Assert.Equal(1, excluded);
    }

    /// <summary>
    /// The excluded day stays in history; it is only held out of the
    /// calculation. Nothing here mutates its input.
    /// </summary>
    [Fact]
    public void OutlierRejection_LeavesItsInputAlone()
    {
        int[] arrivals = [480, 480, 481, 481, 482, 530];

        Statistics.RejectOutliers(arrivals);

        Assert.Equal([480, 480, 481, 481, 482, 530], arrivals);
    }
}
