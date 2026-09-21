// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;
using WhenWillTheBus.Core.Api;

namespace WhenWillTheBus.Core.Model;

/// <summary>
/// Label each scan as the rider getting on or getting off.
/// </summary>
/// <remarks>
/// The API reports scans as bare "ID received" events with a location and a
/// time — IT DOES NOT SAY WHICH DIRECTION. So it is inferred: a scan whose
/// location matches the school is a drop-off in the morning and a pickup in the
/// afternoon; a scan anywhere else (the neighbourhood stop) is the reverse.
/// Where the school name is unknown or does not match, position within the day
/// is used instead, since a normal day alternates pickup, drop-off, pickup,
/// drop-off.
///
/// Observed scan-to-API lag is about 5 minutes 28 seconds. Do not design
/// anything that needs a scan to be instant.
/// </remarks>
public static partial class ScanClassifier
{
    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphanumeric { get; }

    /// <summary>Reduce a place name to comparable lowercase alphanumerics.</summary>
    public static string Normalise(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : NonAlphanumeric.Replace(value.ToLowerInvariant(), string.Empty);

    /// <summary>Return the scans, oldest first, each labelled with a direction.</summary>
    public static IReadOnlyList<ScanEvent> Classify(
        IEnumerable<ScanEvent> scans,
        string? schoolName,
        LocalClock clock)
    {
        string school = Normalise(schoolName);
        Dictionary<DateOnly, int> countsPerDay = [];
        List<ScanEvent> classified = [];

        foreach (ScanEvent scan in scans.OrderBy(scan => scan.Timestamp))
        {
            DateTime local = clock.ToLocal(scan.Timestamp);
            DateOnly day = DateOnly.FromDateTime(local);
            int index = countsPerDay.GetValueOrDefault(day);
            countsPerDay[day] = index + 1;

            string location = Normalise(scan.Location);
            bool morning = local.Hour < Tuning.NoonHour;

            // Accept a substring match either way round: the roster's school
            // name and the scanner's location label are written by different
            // people and rarely agree exactly.
            bool atSchool = school.Length > 0
                && location.Length > 0
                && (location.Contains(school, StringComparison.Ordinal)
                    || school.Contains(location, StringComparison.Ordinal));

            ScanKind kind;
            if (atSchool)
            {
                kind = morning ? ScanKind.Dropoff : ScanKind.Pickup;
            }
            else if (location.Length > 0)
            {
                kind = morning ? ScanKind.Pickup : ScanKind.Dropoff;
            }
            else
            {
                // Nothing to match on, so fall back to alternation within the day.
                kind = index % 2 == 0 ? ScanKind.Pickup : ScanKind.Dropoff;
            }

            classified.Add(scan with { Kind = kind });
        }

        return classified;
    }
}
