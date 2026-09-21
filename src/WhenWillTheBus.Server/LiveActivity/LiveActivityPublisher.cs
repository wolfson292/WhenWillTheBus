// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using WhenWillTheBus.Core;
using WhenWillTheBus.Core.Api;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Notifications;
using WhenWillTheBus.Server.Apns;
using WhenWillTheBus.Server.Devices;

namespace WhenWillTheBus.Server.LiveActivity;

/// <summary>
/// Pushes Live Activity updates when, and only when, <see cref="PushPolicy"/>
/// says they are worth sending.
/// </summary>
/// <remarks>
/// Activities are identified by the JOURNEY id, so a new journey is a new
/// activity and a failed start leaves nothing wedged. Ending and restarting
/// across one journey is avoided: the card should persist through the ride.
/// </remarks>
public sealed class LiveActivityPublisher(
    ApnsClient apns,
    DeviceRegistry registry,
    LocalClock clock,
    ILogger<LiveActivityPublisher> logger)
{
    /// <summary>What was last pushed for each rider, and for which journey.</summary>
    private readonly ConcurrentDictionary<long, (string JourneyId, PublishedActivity State)> _last = [];

    public async Task PublishAsync(
        Student student,
        Journey journey,
        ArrivalPrediction? prediction,
        RiderInfo? info,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        _last.TryGetValue(student.ChildId, out (string JourneyId, PublishedActivity State) previous);
        PublishedActivity? last = previous.State;

        PushDecision decision = PushPolicy.Decide(last, journey, now);
        if (decision == PushDecision.None)
        {
            return;
        }

        // When the journey is over, the activity to end is the one we were
        // publishing to — the journey itself no longer has an id.
        string? journeyId = journey.JourneyId ?? previous.JourneyId;
        if (journeyId is null)
        {
            return;
        }

        IReadOnlyList<RegisteredActivity> activities = registry.ForJourney(journeyId);
        if (activities.Count == 0)
        {
            // Nothing is showing this journey. Remember the decision anyway so a
            // phone that registers mid-journey is not immediately buzzed by a
            // stage change it never saw the other side of.
            _last[student.ChildId] = (journeyId, Snapshot(journey, now));
            return;
        }

        BusActivityState state = BusActivityState.For(
            journey, prediction, info?.DistanceMiles, info?.GpsAgeMinutes, now);

        if (decision == PushDecision.End)
        {
            foreach (RegisteredActivity activity in activities)
            {
                await SendAsync(
                    // Dismiss now. The stage machine held the finished card for its
                    // dwell before reaching idle, so waiting again here just leaves a
                    // stale card on the Lock Screen.
                    () => apns.EndAsync(activity.PushToken, state, now, cancellationToken),
                    activity,
                    cancellationToken).ConfigureAwait(false);

                registry.Forget(activity.PushToken);
            }

            _last.TryRemove(student.ChildId, out _);
            await registry.SaveAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        PushUrgency urgency = decision == PushDecision.TimeSensitive
            ? PushUrgency.TimeSensitive
            : PushUrgency.Passive;

        (string Title, string Body)? alert = urgency == PushUrgency.TimeSensitive
            ? PushPolicy.AlertFor(journey, student.Name, clock)
            : null;

        // A little beyond the next expected push, so a missed update shows as
        // visibly stale rather than silently wrong.
        DateTimeOffset staleAfter = now + PushPolicy.Backstop + TimeSpan.FromMinutes(2);

        foreach (RegisteredActivity activity in activities)
        {
            await SendAsync(
                () => apns.UpdateAsync(activity.PushToken, state, urgency, staleAfter, alert, cancellationToken),
                activity,
                cancellationToken).ConfigureAwait(false);
        }

        _last[student.ChildId] = (journeyId, Snapshot(journey, now));
    }

    private async Task SendAsync(
        Func<Task<PushResult>> send,
        RegisteredActivity activity,
        CancellationToken cancellationToken)
    {
        PushResult result = await send().ConfigureAwait(false);
        if (result.Delivered)
        {
            return;
        }

        if (result.TokenIsDead)
        {
            registry.Forget(activity.PushToken);
            await registry.SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            logger.LogWarning(
                "Live Activity push for {JourneyId} failed: {Reason}", activity.JourneyId, result.Reason);
        }
    }

    private static PublishedActivity Snapshot(Journey journey, DateTimeOffset now) =>
        new(journey.Stage, PushPolicy.Rounded(journey.Target), now);
}
