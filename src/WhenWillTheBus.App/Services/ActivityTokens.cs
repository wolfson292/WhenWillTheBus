// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using UIKit;

namespace WhenWillTheBus.App.Services;

/// <summary>
/// Gets every card's token to the worker, and keeps trying until it has.
/// </summary>
/// <remarks>
/// On 7 Oct the app was opened at 07:48, said hello to the worker, and never
/// registered its card. The token had one chance to be sent -- the moment iOS
/// handed it over -- and a failure there, of any kind, was swallowed and never
/// retried. The card then sat on the Lock Screen counting down to an estimate
/// made before the bus had moved, while the bus arrived early.
///
/// So a token is now PENDING until the worker has accepted it, and every poll
/// tries again. It is also wired up at LAUNCH rather than by a screen: when the
/// worker starts a card by push, iOS wakes the app in the background to hand
/// over that card's token, and a background launch never shows a screen.
/// </remarks>
public static class ActivityTokens
{
    private static readonly ConcurrentDictionary<string, (string JourneyId, long ChildId)> Pending = [];
    private static ServerLink? _server;

    /// <summary>Start listening. Called once, from FinishedLaunching.</summary>
    public static void Start(ServerLink server)
    {
        _server = server;

        LiveActivityBridge.OnPushToken((journeyId, childId, token) =>
        {
            Pending[token] = (journeyId, childId);
            Send(RetryAsync);
        });

        LiveActivityBridge.OnStartToken(token =>
        {
            // Sent at once rather than at the next half-hourly hello: the worker
            // can start no card on this phone until it has it.
            if (PushRegistrar.RememberStartToken(token))
            {
                Send(cancellation => _server.HelloAsync(cancellation));
            }
        });
    }

    /// <summary>Whether a card's token is still waiting to reach the worker.</summary>
    public static bool AnyPending => !Pending.IsEmpty;

    /// <summary>Try every token the worker has not yet accepted. Cheap when there are none.</summary>
    public static async Task RetryAsync(CancellationToken cancellationToken = default)
    {
        if (_server is null)
        {
            return;
        }

        foreach ((string token, (string journeyId, long childId)) in Pending.ToArray())
        {
            try
            {
                if (await _server.RegisterAsync(journeyId, childId, token, cancellationToken))
                {
                    // Only THIS token: a newer one for the same card may have
                    // arrived while the request was in flight.
                    Pending.TryRemove(new KeyValuePair<string, (string, long)>(token, (journeyId, childId)));
                }
            }
            catch (Exception)
            {
                // Kept pending. The next poll, or the next launch, tries again.
            }
        }
    }

    /// <summary>
    /// Run one request with the time iOS allows a backgrounded app to finish.
    /// </summary>
    /// <remarks>
    /// A background launch for a token gets seconds, not minutes, and without
    /// asking for them the request is frozen half-sent the moment iOS suspends
    /// the process. Nothing may escape: this runs from a native callback.
    /// </remarks>
    private static void Send(Func<CancellationToken, Task> work) =>
        _ = Task.Run(async () =>
        {
            nint task = UIApplication.BackgroundTaskInvalid;
            using CancellationTokenSource expiring = new();

            // Ended exactly once, from whichever side gets there first. iOS
            // kills an app whose expiry handler does not end the task itself.
            void End()
            {
                nint ending = Interlocked.Exchange(ref task, UIApplication.BackgroundTaskInvalid);
                if (ending != UIApplication.BackgroundTaskInvalid)
                {
                    UIApplication.SharedApplication.EndBackgroundTask(ending);
                }
            }

            try
            {
                task = UIApplication.SharedApplication.BeginBackgroundTask(
                    "wwtb.register",
                    () =>
                    {
                        expiring.Cancel();
                        End();
                    });

                await work(expiring.Token);
            }
            catch (Exception)
            {
                // Pending tokens are retried; a missed hello goes with the next.
            }
            finally
            {
                End();
            }
        });
}
