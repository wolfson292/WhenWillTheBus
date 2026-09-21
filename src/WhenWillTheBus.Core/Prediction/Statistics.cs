// SPDX-License-Identifier: GPL-3.0-or-later

namespace WhenWillTheBus.Core.Prediction;

/// <summary>
/// The two robust statistics every basis shares: a median that does not lean,
/// and an outlier cut that adapts to how tight a given run actually is.
/// </summary>
public static class Statistics
{
    /// <summary>
    /// The cutoff is the median absolute deviation scaled to a standard
    /// deviation (x1.4826) at three sigma, which adapts to how tight a given
    /// run actually is. 4.45 is that product.
    /// </summary>
    public const double OutlierMadMultiplier = 4.45;

    /// <summary>
    /// Stops the MAD cut being over-eager: a run clustered inside two minutes
    /// would otherwise reject an ordinary five-minute delay as anomalous. In
    /// minutes for clock times, or the same number of seconds for remainders.
    /// </summary>
    public const int OutlierFloorMinutes = 12;

    /// <summary>Below three samples there is nothing to judge an anomaly against.</summary>
    private const int MinimumToJudge = 3;

    /// <summary>
    /// Return the middle of a sorted, non-empty list, averaging an even pair.
    /// </summary>
    /// <remarks>
    /// Taking the upper of two is not a median, it is the later sample — and
    /// with a two-sample minimum on route matching that was the common case,
    /// biasing every route estimate late by however far the two journeys
    /// disagreed. Late is the dangerous direction: it is what leaves a child
    /// at the kerb after the bus has gone.
    /// </remarks>
    public static int Median(IReadOnlyList<int> sortedValues)
    {
        ArgumentOutOfRangeException.ThrowIfZero(sortedValues.Count);

        int middle = sortedValues.Count / 2;
        return sortedValues.Count % 2 != 0
            ? sortedValues[middle]
            : (sortedValues[middle - 1] + sortedValues[middle]) / 2;
    }

    /// <summary>
    /// Drop values far enough from the median to be a bad day rather than a
    /// pattern. Returns what to learn from, and how many were discarded.
    /// </summary>
    /// <param name="sortedValues">Must be sorted ascending.</param>
    /// <param name="floor">
    /// In whatever unit <paramref name="sortedValues"/> is: minutes for clock
    /// arrivals, seconds for the remainders a route estimate works in.
    /// </param>
    /// <remarks>
    /// Occasionally a bus runs badly late — a breakdown, a substitute driver, a
    /// closed road. Those days are real, but they are not the pattern, so they
    /// are excluded from the prediction while still being kept in history.
    ///
    /// With too few samples to judge, everything is kept: two arrivals cannot
    /// tell you which of them is the anomaly. And nothing is ever discarded
    /// wholesale, however strange the data looks.
    /// </remarks>
    public static (IReadOnlyList<int> Kept, int Excluded) RejectOutliers(
        IReadOnlyList<int> sortedValues,
        int floor = OutlierFloorMinutes)
    {
        if (sortedValues.Count < MinimumToJudge)
        {
            return (sortedValues, 0);
        }

        int middle = Median(sortedValues);
        int[] deviations = sortedValues.Select(value => Math.Abs(value - middle)).Order().ToArray();
        double threshold = Math.Max(OutlierMadMultiplier * Median(deviations), floor);

        List<int> kept = sortedValues.Where(value => Math.Abs(value - middle) <= threshold).ToList();
        return kept.Count == 0 ? (sortedValues, 0) : (kept, sortedValues.Count - kept.Count);
    }
}
