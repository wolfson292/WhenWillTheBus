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
/// Tells every phone in the house when a run gets under way -- the morning bus
/// setting off, or the rider boarding at school -- and again when the bus is
/// five minutes from the stop, whether or not anybody opened the app.
/// </summary>
/// <remarks>
/// <see cref="RunAlerts"/> decides WHEN. This decides HOW, phone by phone,
/// using the best channel that phone has:
/// <list type="number">
/// <item>a card it registered for this journey: an update carrying the alert,
/// so the card and the banner say the same thing;</item>
/// <item>a push-to-start token: a NEW card, with the alert, so the Lock Screen
/// has something live on it without the app being touched;</item>
/// <item>a device token: an ordinary notification, so a phone that can do
/// neither still hears it.</item>
/// </list>
///
/// Each step falls through to the next when Apple refuses it. A card that was
/// push-started but whose own token never came back is NOT a card this can
/// reach; the five-minute alert then goes as a notification rather than not at
/// all.
/// </remarks>
public sealed class RunAlerter(
    ApnsClient apns,
    DeviceRegistry registry,
    ClientRegistry clients,
    LocalClock clock,
    ILogger<RunAlerter> logger)
{
    /// <summary>What has gone out, per rider per journey.</summary>
    /// <remarks>
    /// In memory, deliberately: a restart mid-approach repeats an alert at
    /// worst, where persisting it would risk a stale record silencing a
    /// run. Cleared as journeys go by, so it stays a handful of entries.
    /// </remarks>
    private readonly ConcurrentDictionary<(long ChildId, string JourneyId), RunAlert> _sent = [];

    public async Task AlertAsync(
        Student student,
        Journey journey,
        ArrivalPrediction? prediction,
        RiderInfo? info,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (journey.JourneyId is not string journeyId)
        {
            return;
        }

        (long, string) key = (student.ChildId, journeyId);
        RunAlert sent = _sent.GetValueOrDefault(key);
        RunAlert due = RunAlerts.Due(journey, info?.DistanceMiles, now, sent);
        if (due == RunAlert.None)
        {
            return;
        }

        // Recorded BEFORE sending. A push Apple refuses is logged and not
        // retried every thirty seconds until the bus arrives: a repeated buzz
        // is the failure this whole design keeps having to avoid.
        _sent[key] = sent | due;
        Forget(student.ChildId, journeyId);

        (string Title, string Body) alert = RunAlerts.TextFor(due, journey, student.Name, clock);
        BusActivityState state = BusActivityState.For(
            journey, prediction, info?.DistanceMiles, info?.GpsAgeMinutes, now);
        DateTimeOffset staleAfter = now + PushPolicy.Backstop + TimeSpan.FromMinutes(2);

        logger.LogInformation(
            "Run alert for {JourneyId}: {Alert} ({Title})", journeyId, due, alert.Title);

        // 1. Phones with a card for this journey hear it on the card.
        HashSet<string> reached = [];
        bool anonymousCard = false;
        foreach (RegisteredActivity card in registry.ForJourney(journeyId).Where(card => card.ChildId == student.ChildId))
        {
            PushResult result = await apns.UpdateAsync(
                card.PushToken, state, PushUrgency.TimeSensitive, staleAfter, alert, card.Sandbox,
                cancellationToken, sound: true).ConfigureAwait(false);

            if (result.Delivered)
            {
                if (card.DeviceId is string device)
                {
                    reached.Add(device);
                }
                else
                {
                    anonymousCard = true;
                }

                continue;
            }

            if (result.TokenIsDead)
            {
                registry.Forget(card.PushToken);
                await registry.SaveAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // A card from a build too old to say which phone it is on cannot be
        // matched to one, so every phone is also alerted below. Two banners on
        // one phone is the lesser fault next to none on another.
        if (anonymousCard)
        {
            logger.LogInformation("A card for {JourneyId} did not say which phone it is on", journeyId);
        }

        BusActivityIdentity identity = new(journeyId, student.Name, student.BusNumber, student.ChildId);
        bool forgotAToken = false;

        foreach (ClientIdentity phone in clients.All)
        {
            if (reached.Contains(phone.Id))
            {
                continue;
            }

            // 2. Start a card. Only at the start: starting one at the
            //    five-minute mark on a phone that already has a push-started
            //    card whose token never came back would put two on its screen.
            if (due.HasFlag(RunAlert.Started) && phone.StartToken is string startToken)
            {
                PushResult started = await apns.StartAsync(
                    startToken, identity, state, alert, staleAfter, phone.Sandbox, cancellationToken)
                    .ConfigureAwait(false);

                if (started.Delivered)
                {
                    continue;
                }

                if (started.TokenIsDead)
                {
                    forgotAToken |= clients.ForgetStartToken(phone.Id);
                }
            }

            // 3. Anything else that can still be told, is.
            if (phone.DeviceToken is string deviceToken)
            {
                PushResult told = await apns.AlertAsync(
                    deviceToken, alert.Title, alert.Body, sandbox: phone.Sandbox,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (!told.Delivered)
                {
                    logger.LogWarning(
                        "Run alert to client {Id} failed: {Reason}",
                        ClientRegistry.Short(phone.Id), told.Reason);
                }
            }
        }

        if (forgotAToken)
        {
            await clients.SaveAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Drop what was sent for this rider's earlier journeys.</summary>
    private void Forget(long childId, string current)
    {
        foreach ((long ChildId, string JourneyId) old in _sent.Keys)
        {
            if (old.ChildId == childId && old.JourneyId != current)
            {
                _sent.TryRemove(old, out _);
            }
        }
    }
}
