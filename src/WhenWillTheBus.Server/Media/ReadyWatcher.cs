// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core;
using WhenWillTheBus.Server.Apns;
using WhenWillTheBus.Server.Devices;

namespace WhenWillTheBus.Server.Media;

/// <summary>
/// Tells whoever something was asked for that it can now be watched.
/// </summary>
/// <remarks>
/// POLLED, NOT HOOKED. Radarr and Sonarr can call a webhook on import, but
/// that is configuration living in two other apps, a route from their
/// network into this one, and a failure mode where a renamed URL silently
/// stops every message. Asking them every few minutes about the handful of
/// things still outstanding needs nothing but the API keys already here.
/// </remarks>
public sealed class ReadyWatcher(
    ArrClient arr,
    RequestLog log,
    ClientRegistry clients,
    ApnsClient apns,
    LocalClock clock,
    ILogger<ReadyWatcher> logger) : BackgroundService
{
    /// <summary>How often the library is asked. A film is not watched the minute it lands.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "Checking for finished downloads failed; will try again");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One pass over everything still outstanding.</summary>
    public async Task CheckAsync(CancellationToken token)
    {
        DateTimeOffset now = clock.Now;
        bool changed = false;

        // One question per title, and one message per person per title: the
        // same film asked for twice is still one film arriving.
        foreach (IGrouping<(MediaKind, long), MediaRequest> title in log.Waiting(now)
                     .GroupBy(request => (request.Kind, request.RemoteId)))
        {
            Readiness? readiness = await arr.ReadinessAsync(title.Key.Item1, title.Key.Item2, token)
                .ConfigureAwait(false);
            if (readiness is null)
            {
                continue;
            }

            foreach (IGrouping<string?, MediaRequest> person in title.GroupBy(request => request.RecipientId))
            {
                // The latest ask decides the wording -- it is the one that said
                // whose it is -- and every ask for it is marked the same.
                MediaRequest latest = person.MaxBy(request => request.RequestedAt)!;
                ReadyStep step = ReadyAlerts.Next(latest, readiness);
                if (step is ReadyStep.None)
                {
                    continue;
                }

                await TellAsync(person.Key!, ReadyAlerts.TextFor(step, latest, readiness), token)
                    .ConfigureAwait(false);

                foreach (MediaRequest request in person)
                {
                    log.Update(request, step is ReadyStep.Ready
                        ? request with { ReadyAt = now, StartedAt = request.StartedAt ?? now }
                        : request with { StartedAt = now });
                }

                logger.LogInformation("{Title}: {Step}", latest.Title, step);
                changed = true;
            }
        }

        if (changed)
        {
            await log.SaveAsync(token).ConfigureAwait(false);
        }
    }

    private async Task TellAsync(string clientId, (string Title, string Body) message, CancellationToken token)
    {
        ClientIdentity? phone = clients.Find(clientId);
        if (phone?.DeviceToken is not string deviceToken)
        {
            // Marked as told anyway. A phone that has not allowed notifications
            // will see it in the list; retrying every five minutes for a month
            // would change nothing.
            logger.LogInformation(
                "Nobody to tell about {Title}: client {Id} has no notification token",
                message.Title, ClientRegistry.Short(clientId));
            return;
        }

        PushResult result = await apns.AlertAsync(
            deviceToken, message.Title, message.Body, sandbox: phone.Sandbox, cancellationToken: token)
            .ConfigureAwait(false);

        if (!result.Delivered)
        {
            logger.LogWarning(
                "Could not tell client {Id} something is ready: {Reason}",
                ClientRegistry.Short(clientId), result.Reason);
        }
    }
}
