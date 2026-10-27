// SPDX-License-Identifier: GPL-3.0-or-later

namespace WhenWillTheBus.Server.Travel;

/// <summary>Whether to leave yet, put as a parent would want it put.</summary>
public enum MeetVerdict
{
    /// <summary>There is time. Leave by the time given.</summary>
    Wait,

    /// <summary>Leave now to be at the stop with the margin to spare.</summary>
    LeaveNow,

    /// <summary>Leaving now still arrives before the bus, but inside the margin.</summary>
    Hurry,

    /// <summary>Even leaving now arrives after the bus.</summary>
    TooLate,
}

/// <summary>The answer to "can I get to the stop before the bus?".</summary>
public sealed record MeetPlan(MeetVerdict Verdict, DateTimeOffset LeaveBy, int LeaveInMinutes, string Message);

/// <summary>When to leave to be at the stop before the bus, from a drive time and the bus's estimate.</summary>
public static class MeetTheBus
{
    public static MeetPlan Decide(DateTimeOffset now, DateTimeOffset busDue, DriveTime drive, TimeSpan margin, Func<DateTimeOffset, string> clock)
    {
        TimeSpan driving = TimeSpan.FromSeconds(drive.Seconds);
        DateTimeOffset leaveBy = busDue - driving - margin;
        DateTimeOffset arriving = now + driving;
        int leaveIn = (int)Math.Floor((leaveBy - now).TotalMinutes);
        int minutes = (int)Math.Round(driving.TotalMinutes);

        // Said in the sentence, because "12 minutes" means something different
        // on an empty road at 6am and in this morning's traffic.
        string basis = drive.TrafficSeconds is int traffic && traffic >= 60
            ? $"{minutes} min drive, {traffic / 60} of it traffic"
            : drive.Source == "tomtom" ? $"{minutes} min drive in current traffic" : $"{minutes} min drive, empty roads";

        if (arriving > busDue)
        {
            int behind = (int)Math.Ceiling((arriving - busDue).TotalMinutes);
            return new MeetPlan(MeetVerdict.TooLate, leaveBy, leaveIn,
                $"You'd get there about {behind} min after the bus ({basis}). Bus due {clock(busDue)}.");
        }

        if (now >= leaveBy)
        {
            bool tight = arriving > busDue - margin;
            return new MeetPlan(tight ? MeetVerdict.Hurry : MeetVerdict.LeaveNow, leaveBy, leaveIn,
                tight
                    ? $"Leave now -- you'll only just make it ({basis}). Bus due {clock(busDue)}."
                    : $"Leave now ({basis}). Bus due {clock(busDue)}.");
        }

        // A countdown only when it is close enough to act on: asked the night
        // before, "in 550 min" is a sum to do rather than an answer.
        string within = leaveIn < 60 ? $", in {Math.Max(leaveIn, 1)} min" : string.Empty;
        return new MeetPlan(MeetVerdict.Wait, leaveBy, leaveIn,
            $"Leave by {clock(leaveBy)}{within} ({basis}). Bus due {clock(busDue)}.");
    }
}
