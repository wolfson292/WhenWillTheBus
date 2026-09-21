// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;

namespace WhenWillTheBus.Core.Model;

/// <summary>Parses the timetable strings the roster carries, such as "7:56 A.M.".</summary>
public static partial class StopTime
{
    [GeneratedRegex(@"(?<hour>\d{1,2}):(?<minute>\d{2})\s*(?<meridiem>[AaPp])", RegexOptions.None)]
    private static partial Regex Pattern { get; }

    /// <summary>Parse a scheduled stop time, or null if it is not one.</summary>
    public static TimeOnly? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        Match match = Pattern.Match(value);
        if (!match.Success)
        {
            return null;
        }

        int hour = int.Parse(match.Groups["hour"].ValueSpan) % 12;
        if (char.ToLowerInvariant(match.Groups["meridiem"].ValueSpan[0]) == 'p')
        {
            hour += 12;
        }

        int minute = int.Parse(match.Groups["minute"].ValueSpan);
        return minute > 59 ? null : new TimeOnly(hour, minute);
    }
}
