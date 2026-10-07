// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core.Model;

namespace WhenWillTheBus.Core.Notifications;

/// <summary>The two things worth telling a household about each school run.</summary>
[Flags]
public enum RunAlert
{
    None = 0,

    /// <summary>
    /// The run is under way: the morning bus has set off for the stop, or the
    /// rider has boarded at school for the ride home. Starts the card on any
    /// phone that has none.
    /// </summary>
    Started = 1,

    /// <summary>The bus is five minutes from the stop: time to go out.</summary>
    FiveMinutes = 2,
}

/// <summary>
/// Decides when the worker interrupts somebody about a school run.
/// </summary>
/// <remarks>
/// SEPARATE FROM <see cref="PushPolicy"/>, which decides when a card already on
/// a phone is worth refreshing and only ever buzzes on a stage change. These
/// alerts are sent whether or not anybody opened the app, because the whole
/// point is that nobody should have to: on 7 Oct the app was opened at 07:48,
/// its card never reached the worker, and the bus arrived while the family was
/// still going by an estimate made before it had moved. That afternoon nobody
/// opened it at all, and there was no card for the ride home.
///
/// THE TWO RUNS START DIFFERENTLY. In the morning nobody is aboard until the
/// bus reaches the stop, so the run starts when the bus SETS OFF towards it.
/// In the afternoon the boarding scan at school is the start, and the bus
/// then heads for the same stop -- so both runs end with the same five-minute
/// warning before it gets there.
///
/// Each is sent ONCE per journey; the caller remembers what has gone out.
/// Pure, so every run can be replayed against it without a phone.
/// </remarks>
public static class RunAlerts
{
    /// <summary>The alerts due now that have not already been sent for this journey.</summary>
    /// <param name="sent">What has already gone out for this journey.</param>
    public static RunAlert Due(
        Journey journey,
        double? distanceMiles,
        DateTimeOffset now,
        RunAlert sent)
    {
        // Heading for the rider's stop, and nothing else. At the stop, on the
        // ride to school, and home, the stage alerts already say it.
        bool morning = journey.Stage == JourneyStage.ToStop;
        bool afternoon = journey.Stage == JourneyStage.FromSchool;
        if (!(morning || afternoon) || journey.JourneyId is null)
        {
            return RunAlert.None;
        }

        bool finalDue = journey.Target is not null && journey.Target.Value - now <= Tuning.FinalAlertLead;

        // Aboard IS under way. In the morning the bus has to be seen to leave
        // where it waits; the five-minute mark implies it has, or a morning
        // with no GPS for the last stretch -- 5 Oct went nineteen minutes
        // without a fix -- would never start a card at all, and would say
        // "five minutes" to a phone with nothing on its Lock Screen.
        bool underway = afternoon
            || (distanceMiles is double miles && miles <= Tuning.DepartedMiles)
            || finalDue;

        RunAlert due = RunAlert.None;
        if (underway && !sent.HasFlag(RunAlert.Started))
        {
            due |= RunAlert.Started;
        }

        if (finalDue && !sent.HasFlag(RunAlert.FiveMinutes))
        {
            due |= RunAlert.FiveMinutes;
        }

        return due;
    }

    /// <summary>
    /// What the alert says. When both are due at once they go out as ONE alert
    /// in the more urgent wording: two buzzes a second apart is noise.
    /// </summary>
    public static (string Title, string Body) TextFor(
        RunAlert alert,
        Journey journey,
        string riderName,
        LocalClock clock)
    {
        string? at = journey.Target is DateTimeOffset target
            ? clock.ToLocal(target).ToString("h:mm tt")
            : null;
        string due = at is null ? string.Empty : $" — due at {at}";
        bool home = journey.Stage == JourneyStage.FromSchool;

        if (alert.HasFlag(RunAlert.FiveMinutes))
        {
            return home
                ? ("5 minutes from home", $"{riderName}'s bus is almost at the stop{due}.")
                : ("5 minutes away", $"{riderName}'s bus is almost here{due}.");
        }

        return home
            ? ("On the bus home", at is null
                ? $"{riderName} is aboard the bus home."
                : $"{riderName} is aboard, home around {at}.")
            : ("Bus is on the way", $"{riderName}'s bus has set off{due}.");
    }
}
