// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Prediction;

namespace WhenWillTheBus.Core.Api;

/// <summary>
/// Turns the two roster endpoints into riders.
/// </summary>
/// <remarks>
/// <c>getUserInfo</c> returns <c>childBuses</c> and <c>getAllRiders</c> returns
/// <c>allRiders</c>, and THE TWO SHARE NO IDENTIFIER. So riders are claimed by
/// bus number plus scheduled stop time first, then by bus number alone, and only
/// then by list position. Each rider can be claimed once, which keeps two
/// children on the same bus from collapsing onto one record.
/// </remarks>
public static class RosterReader
{
    /// <summary>Join <c>childBuses</c> entries to their <c>allRiders</c> records.</summary>
    public static Dictionary<long, Student> ReadRoster(JsonElement userInfo, JsonElement allRiders)
    {
        List<JsonElement> buses = userInfo.Array("childBuses")
            .Where(bus => bus.Long("childId") is not null)
            .ToList();

        IReadOnlyList<JsonElement> riders = allRiders.Array("allRiders");

        List<int> unclaimed = [.. Enumerable.Range(0, riders.Count)];
        Dictionary<int, int> claimed = [];

        // Strict first — bus number AND stop time — then loosened to bus number
        // alone. A rider is only claimed when exactly one candidate matches, so
        // an ambiguous pairing falls through to the next round rather than
        // guessing.
        foreach (bool strict in (ReadOnlySpan<bool>)[true, false])
        {
            for (int position = 0; position < buses.Count; position++)
            {
                if (claimed.ContainsKey(position))
                {
                    continue;
                }

                string busNumber = buses[position].String("busNo") ?? string.Empty;
                string busTime = ScanClassifier.Normalise(buses[position].String("busTime"));

                List<int> matches = unclaimed
                    .Where(index => BusNumbers(riders[index]).Contains(busNumber)
                        && (!strict
                            || (busTime.Length > 0
                                && StopTimes(riders[index]).Any(time =>
                                    time.StartsWith(busTime, StringComparison.Ordinal)))))
                    .ToList();

                if (matches.Count == 1)
                {
                    claimed[position] = matches[0];
                    unclaimed.Remove(matches[0]);
                }
            }
        }

        // Anything still unpaired takes the next rider by position.
        for (int position = 0; position < buses.Count; position++)
        {
            if (!claimed.ContainsKey(position) && unclaimed.Count > 0)
            {
                claimed[position] = unclaimed[0];
                unclaimed.RemoveAt(0);
            }
        }

        Dictionary<long, Student> students = [];
        for (int position = 0; position < buses.Count; position++)
        {
            JsonElement bus = buses[position];
            JsonElement rider = claimed.TryGetValue(position, out int index) ? riders[index] : default;
            long childId = bus.Long("childId")!.Value;
            students[childId] = Build(childId, bus, rider);
        }

        return students;
    }

    private static HashSet<string> BusNumbers(JsonElement rider) =>
        [.. new[] { "amBusNo", "pmBusNo", "latePmBusNo" }
            .Select(key => rider.String(key))
            .Where(value => !string.IsNullOrEmpty(value))
            .Select(value => value!)];

    private static HashSet<string> StopTimes(JsonElement rider) =>
        [.. new[] { "amStopTime", "pmStopTime", "latePmStopTime" }
            .Select(key => rider.String(key))
            .Where(value => !string.IsNullOrEmpty(value))
            .Select(ScanClassifier.Normalise)];

    private static Student Build(long childId, JsonElement bus, JsonElement rider)
    {
        string? busNumber = bus.String("busNo") ?? rider.String("amBusNo") ?? rider.String("pmBusNo");

        // The AM and PM stop are the same place for most riders; prefer whichever
        // one actually carries coordinates.
        double? latitude = rider.Double("amStopLat") ?? rider.Double("pmStopLat");
        double? longitude = rider.Double("amStopLon") ?? rider.Double("pmStopLon");

        string? substitute = bus.String("sub")?.Trim();

        return new Student
        {
            ChildId = childId,
            Name = rider.String("riderName") ?? $"Rider {childId}",
            StudentId = rider.String("studentId"),
            BusNumber = busNumber,
            RouteNumber = bus.String("routeNo"),
            SubstituteBus = string.IsNullOrEmpty(substitute) ? null : substitute,
            SchoolName = rider.String("schoolName"),
            AmScheduled = StopTime.Parse(rider.String("amStopTime")),
            PmScheduled = StopTime.Parse(rider.String("pmStopTime")),
            StopAddress = rider.String("amStopAddress") ?? rider.String("pmStopAddress"),
            Stop = latitude is null || longitude is null || (latitude == 0 && longitude == 0)
                ? null
                : new GeoPoint(latitude.Value, longitude.Value),
        };
    }

    /// <summary>
    /// Read <c>getStudentScan</c> into per-rider events.
    /// </summary>
    /// <remarks>
    /// The scan endpoint keys students by NAME rather than by child id, so names
    /// are compared with punctuation and case removed. Middle names in the roster
    /// ("Robin Alex Rivera" against "Robin Rivera") mean a first-and-last match
    /// is used as a fallback.
    ///
    /// Events come back UNCLASSIFIED. The caller merges them into the rider's
    /// accumulated history first and classifies the whole window at once, because
    /// the alternation fallback needs to see a full day.
    /// </remarks>
    public static Dictionary<long, List<ScanEvent>> ReadScans(
        JsonElement payload,
        IReadOnlyDictionary<long, Student> students)
    {
        Dictionary<long, List<ScanEvent>> byChild = [];
        IReadOnlyList<JsonElement> details = payload.Array("studentDetails");
        if (details.Count == 0)
        {
            return byChild;
        }

        Dictionary<string, Student> byName = [];
        foreach (Student student in students.Values)
        {
            byName[ScanClassifier.Normalise(student.Name)] = student;
        }

        foreach (JsonElement detail in details)
        {
            string scanName = ScanClassifier.Normalise(detail.String("studentName"));
            Student? student = byName.GetValueOrDefault(scanName)
                ?? MatchByNameParts(scanName, students);

            // A single-child account with a single scanned student: the two names
            // must refer to the same rider whatever they are spelled like.
            if (student is null && students.Count == 1 && details.Count == 1)
            {
                student = students.Values.First();
            }

            if (student is null)
            {
                continue;
            }

            List<ScanEvent> events = [];
            foreach (JsonElement scan in detail.Array("studentScans"))
            {
                long? scanTime = scan.Long("scanTime");
                if (scanTime is null)
                {
                    continue;
                }

                events.Add(new ScanEvent(
                    DateTimeOffset.FromUnixTimeSeconds(scanTime.Value),
                    scan.String("scanLocation"),

                    // Direction is inferred later, by ScanClassifier.
                    ScanKind.Pickup,
                    scan.String("scanMethod")));
            }

            byChild[student.ChildId] = events;
        }

        return byChild;
    }

    private static Student? MatchByNameParts(string scanName, IReadOnlyDictionary<long, Student> students)
    {
        if (scanName.Length == 0)
        {
            return null;
        }

        foreach (Student student in students.Values)
        {
            string[] parts = student.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            string shortened = ScanClassifier.Normalise($"{parts[0]}{parts[^1]}");
            if (shortened.Length > 0 && shortened == scanName)
            {
                return student;
            }
        }

        return null;
    }
}
