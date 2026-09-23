// SPDX-License-Identifier: GPL-3.0-or-later

namespace WhenWillTheBus.Core.Model;

/// <summary>What the big number on the screen is showing.</summary>
public enum HeadlineKind
{
    /// <summary>Nothing worth a time. The stage line carries the meaning instead.</summary>
    None,

    /// <summary>An instant to count towards.</summary>
    Time,

    /// <summary>It is happening. A countdown to it would be counting to zero.</summary>
    Now,
}

/// <summary>
/// Which time a screen should lead with.
/// </summary>
/// <param name="Moment">Set only when <see cref="Kind"/> is <see cref="HeadlineKind.Time"/>.</param>
public readonly record struct Headline(HeadlineKind Kind, DateTimeOffset? Moment)
{
    private static readonly Headline Nothing = new(HeadlineKind.None, null);
    private static readonly Headline Happening = new(HeadlineKind.Now, null);

    /// <summary>
    /// Decide what to lead with.
    /// </summary>
    /// <param name="nextArrival">
    /// The engine's estimate for the next arrival AT THE HOME STOP — which is a
    /// different question from "what is happening now", and the confusion
    /// between the two has produced the same fault twice.
    /// </param>
    /// <remarks>
    /// THE PREDICTION ROLLS ON THE MOMENT A RUN IS UNDER WAY. It answers "when
    /// does the bus next reach the home stop", so once today's morning arrival
    /// has been counted it describes the AFTERNOON. Any live journey that
    /// borrows it is therefore showing a time belonging to a different journey,
    /// and it is always wrong by most of a day.
    ///
    /// Three cases, and each is a different question:
    /// <list type="bullet">
    /// <item>the bus is AT the stop — the arrival it would count towards has
    /// just happened, so the honest answer is "now";</item>
    /// <item>the rider is ABOARD — the time that matters is where the ride
    /// ends, and if that has never been learned there is no number to give;</item>
    /// <item>otherwise — waiting, or the day is done — the next arrival at the
    /// stop is exactly the question being asked.</item>
    /// </list>
    /// </remarks>
    public static Headline For(Journey journey, DateTimeOffset? nextArrival) => journey.Stage switch
    {
        // 23 Sep, 08:07: the bus pulled up and the screen read "The bus is at
        // the stop" above "5:21 PM, in 9h 13m". The five minutes between a bus
        // arriving and the boarding scan landing is exactly when somebody is
        // looking at this.
        JourneyStage.AtStop => Happening,

        JourneyStage.ToSchool or JourneyStage.FromSchool =>
            journey.Target is DateTimeOffset end ? new Headline(HeadlineKind.Time, end) : Nothing,

        _ => nextArrival is DateTimeOffset next ? new Headline(HeadlineKind.Time, next) : Nothing,
    };
}
