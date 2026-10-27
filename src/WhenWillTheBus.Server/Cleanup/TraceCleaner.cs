// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.Options;
using WhenWillTheBus.Core;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Prediction;

namespace WhenWillTheBus.Server.Cleanup;

/// <summary>
/// Tidies finished runs a few at a time: matches each to the roads, then has
/// it reviewed.
/// </summary>
/// <remarks>
/// Called from the monitor's own loop, between bus runs, so nothing here ever
/// competes with a live journey for the worker's attention -- and so it amends
/// history on the same thread that writes it.
///
/// Newest first, so today's run is tidied before a backlog from September. A
/// run is matched once and reviewed once: a failed match is recorded as one
/// (confidence 0) rather than retried on every pass.
/// </remarks>
public sealed class TraceCleaner(
    PredictionEngine engine,
    RoadMatcher roads,
    RunReviewer reviewer,
    LocalClock clock,
    IOptions<CleanupOptions> options,
    ILogger<TraceCleaner> logger)
{
    private DateTimeOffset _lastPass = DateTimeOffset.MinValue;

    /// <summary>One pass, if one is due. Returns whether history changed.</summary>
    public async Task<bool> PassAsync(IEnumerable<long> riders, DateTimeOffset now, CancellationToken token)
    {
        if ((!roads.Enabled && !reviewer.Enabled) || now - _lastPass < options.Value.Interval)
        {
            return false;
        }

        _lastPass = now;
        bool changed = false;

        foreach (long childId in riders)
        {
            changed |= await MatchAsync(childId, token).ConfigureAwait(false);
            changed |= await ReviewAsync(childId, token).ConfigureAwait(false);
        }

        return changed;
    }

    private async Task<bool> MatchAsync(long childId, CancellationToken token)
    {
        if (!roads.Enabled)
        {
            return false;
        }

        List<RunArrival> due = [.. engine.ArrivalsFor(childId)
            .Where(arrival => arrival.Track.Count > 1 && arrival.RoadConfidence is null)
            .OrderByDescending(arrival => arrival.Arrival)
            .Take(options.Value.PerPass)];

        int matched = 0;
        foreach (RunArrival arrival in due)
        {
            try
            {
                (IReadOnlyList<GeoPoint> road, double confidence) = await roads.MatchAsync(arrival, token).ConfigureAwait(false);
                engine.Amend(childId, arrival.Run, arrival.Arrival, item => item with { Road = road, RoadConfidence = confidence });
                matched++;
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                // The roads server is down or rebuilding. Nothing is lost; the
                // next pass tries again.
                logger.LogWarning("Road matching unavailable: {Message}", error.Message);
                break;
            }
        }

        if (matched > 0)
        {
            logger.LogInformation("Matched {Count} run(s) to roads", matched);
        }

        return matched > 0;
    }

    private async Task<bool> ReviewAsync(long childId, CancellationToken token)
    {
        if (!reviewer.Enabled)
        {
            return false;
        }

        IReadOnlyList<RunArrival> history = engine.ArrivalsFor(childId);

        // After matching where matching runs, so the review sees the road.
        List<RunArrival> due = [.. history
            .Where(arrival => arrival.Track.Count > 1
                && arrival.Review is null
                && (arrival.RoadConfidence is not null || !roads.Enabled))
            .OrderByDescending(arrival => arrival.Arrival)
            .Take(options.Value.PerPass)];

        int reviewed = 0;
        foreach (RunArrival arrival in due)
        {
            RunSummary summary = RunSummary.Of(arrival, history, clock);

            RunReview? review;
            try
            {
                review = await reviewer.ReviewAsync(summary, token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
            {
                logger.LogWarning("Run review unavailable: {Message}", error.Message);
                break;
            }

            if (review is null)
            {
                continue;
            }

            engine.Amend(childId, arrival.Run, arrival.Arrival, item => item with { Review = review });
            logger.LogInformation(
                "Reviewed {Run} {Date:yyyy-MM-dd}: {Verdict}", arrival.Run, summary.Date, review.Verdict);
            reviewed++;
        }

        return reviewed > 0;
    }
}
