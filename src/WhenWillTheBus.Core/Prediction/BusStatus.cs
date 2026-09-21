// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;
using WhenWillTheBus.Core.Model;

namespace WhenWillTheBus.Core.Prediction;

/// <summary>
/// The GPS freshness string, and why it is critical.
/// </summary>
/// <remarks>
/// <c>stsMsg</c> is written for humans — "current", "3 min. ago", "12 mins
/// ago", "inactive" — and it changes every minute while a bus runs, which makes
/// it useless as a display value. It is split into a bounded state and an age.
///
/// A STALE READING IS THE LAST KNOWN POSITION REPEATED, NOT A NEW ONE. That one
/// fact is responsible for two separate bugs in the reference implementation:
/// a frozen feed walked the published arrival later by one second per second,
/// and sixty identical points written into a track made every later journey
/// through that spot unmatchable. Treat stale as "I know where it was N minutes
/// ago", never as "I know where it is".
/// </remarks>
public static partial class BusStatus
{
    /// <summary>"3 min. ago", "12 mins ago" — the number is the age of the fix.</summary>
    [GeneratedRegex(@"(\d+)\s*min", RegexOptions.IgnoreCase)]
    private static partial Regex AgePattern { get; }

    /// <summary>
    /// Split the API's status string into a bounded state and a GPS age in
    /// minutes.
    /// </summary>
    /// <returns>
    /// <see cref="BusStatusKind.Unknown"/> for anything unrecognised, so a
    /// vocabulary the API adds later reads as unknown rather than crashing.
    /// </returns>
    public static (BusStatusKind Kind, int? AgeMinutes) Parse(string? statusMessage)
    {
        string text = statusMessage?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return (BusStatusKind.Unknown, null);
        }

        if (text.StartsWith("current", StringComparison.OrdinalIgnoreCase))
        {
            return (BusStatusKind.Current, 0);
        }

        if (text.StartsWith("inactive", StringComparison.OrdinalIgnoreCase))
        {
            return (BusStatusKind.Inactive, null);
        }

        Match match = AgePattern.Match(text);
        return match.Success
            ? (BusStatusKind.Stale, int.Parse(match.Groups[1].ValueSpan))
            : (BusStatusKind.Unknown, null);
    }
}
