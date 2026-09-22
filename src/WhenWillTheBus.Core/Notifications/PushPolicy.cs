// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core.Model;

namespace WhenWillTheBus.Core.Notifications;

/// <summary>What to do about the Live Activity on this poll.</summary>
public enum PushDecision
{
    /// <summary>Nothing has changed that is worth saying. This is the usual answer.</summary>
    None,

    /// <summary>Update the card quietly — no alert, low priority, no haptic.</summary>
    Passive,

    /// <summary>Something changed the reader wants to know now. ONLY a stage change earns this.</summary>
    TimeSensitive,

    /// <summary>The journey is over; take the card away.</summary>
    End,
}

/// <summary>What was last pushed for a journey, so the next decision has something to compare against.</summary>
/// <param name="Target">Already rounded to the minute — see <see cref="PushPolicy"/>.</param>
public sealed record PublishedActivity(JourneyStage Stage, DateTimeOffset? Target, DateTimeOffset PushedAt);

/// <summary>
/// Decides when a Live Activity is worth updating.
/// </summary>
/// <remarks>
/// This exists because publishing every recomputation drove TWENTY-FIVE
/// notification pushes in twenty minutes, which leaks haptics onto a watch and
/// spends the iOS update budget to say the same thing over and over.
///
/// Three reasons to push, and only three:
/// <list type="bullet">
/// <item>the stage changed — that is news, and the only thing that may interrupt;</item>
/// <item>the target moved to a different displayed minute;</item>
/// <item>a short periodic backstop, so a card cannot sit indefinitely stale.</item>
/// </list>
///
/// A BACKSTOP ALONE IS NOT ENOUGH: the chronometer ticks on the phone without a
/// push, so if the target moves and nothing says so, the card counts down to the
/// old time and is confidently wrong at exactly the wrong moment.
///
/// Pure, so the whole policy can be tested at any instant without a phone.
/// </remarks>
public static class PushPolicy
{
    /// <summary>
    /// How long a card may go without an update before one is sent anyway.
    /// </summary>
    /// <remarks>
    /// Long enough that a quiet approach is not a stream of pushes, short enough
    /// that a card is never badly out of date.
    /// </remarks>
    public static readonly TimeSpan Backstop = TimeSpan.FromMinutes(5);

    /// <summary>Decide what this journey's activity needs.</summary>
    /// <param name="last">What was last pushed, or null if nothing has been.</param>
    public static PushDecision Decide(
        PublishedActivity? last,
        Journey journey,
        DateTimeOffset now,
        TimeSpan? backstop = null)
    {
        // The journey is over. The stage machine has already held the finished
        // card for its dwell, so reaching idle is the signal to clear it.
        if (!journey.Active)
        {
            return last is null ? PushDecision.None : PushDecision.End;
        }

        // Nothing has been pushed yet. The app starts the activity locally and
        // it already shows the current state, so the first server-side update is
        // a quiet confirmation rather than news.
        if (last is null)
        {
            return PushDecision.Passive;
        }

        if (last.Stage != journey.Stage)
        {
            return PushDecision.TimeSensitive;
        }

        // The target is compared AS DISPLAYED. Raw, it carries sub-second
        // precision and moves every poll, so watching it would push every thirty
        // seconds to say the same thing.
        if (Rounded(last.Target) != Rounded(journey.Target))
        {
            return PushDecision.Passive;
        }

        return now - last.PushedAt >= (backstop ?? Backstop) ? PushDecision.Passive : PushDecision.None;
    }

    /// <summary>What a reader would actually see, which is minutes.</summary>
    public static DateTimeOffset? Rounded(DateTimeOffset? moment) =>
        moment is null ? null : Prediction.JourneyStageMachine.ToTheMinute(moment.Value);

    /// <summary>
    /// The one-line alert a stage change carries. Returns null for stages that
    /// are not worth interrupting for.
    /// </summary>
    /// <remarks>
    /// LEAD WITH THE TIME, NOT THE DISTANCE. A watch truncates hard, and the
    /// distance is already drawn as the progress bar — "4.5 miles from…" spent
    /// the whole visible line restating the bar.
    /// </remarks>
    public static (string Title, string Body)? AlertFor(Journey journey, string riderName, LocalClock clock) =>
        journey.Stage switch
        {
            JourneyStage.ToStop when journey.Target is not null =>
                ("Bus on the way", $"{riderName}'s bus is due at {Time(journey.Target.Value, clock)}."),
            JourneyStage.AtStop => ("Bus is here", $"The bus has reached {riderName}'s stop."),
            JourneyStage.ToSchool when journey.Target is not null =>
                ("On the bus", $"{riderName} is aboard, at school around {Time(journey.Target.Value, clock)}."),

            // Boarding is worth saying even when nothing has been learned about
            // when the ride ends. Staying silent until a school-arrival time
            // exists withholds the most useful notification of the morning on
            // exactly the days it has never been sent before.
            JourneyStage.ToSchool => ("On the bus", $"{riderName} is aboard the bus to school."),
            JourneyStage.AtSchool => ("At school", $"{riderName} was dropped off safely."),
            JourneyStage.FromSchool when journey.Target is not null =>
                ("Heading home", $"{riderName} is aboard, home around {Time(journey.Target.Value, clock)}."),
            JourneyStage.FromSchool => ("Heading home", $"{riderName} is aboard the bus home."),
            JourneyStage.Home => ("Home", $"{riderName} is off the bus."),
            _ => null,
        };

    private static string Time(DateTimeOffset moment, LocalClock clock) => clock.ToLocal(moment).ToString("h:mm tt");
}
