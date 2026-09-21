// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core.Api;
using WhenWillTheBus.Core.Prediction;

namespace WhenWillTheBus.Core.Model;

/// <summary>Everything known about one rider.</summary>
public sealed record Student
{
    public required long ChildId { get; init; }

    public required string Name { get; init; }

    public string? StudentId { get; init; }

    public string? BusNumber { get; init; }

    public string? RouteNumber { get; init; }

    /// <summary>The API names a replacement vehicle here when one is covering the route.</summary>
    public string? SubstituteBus { get; init; }

    public string? SchoolName { get; init; }

    /// <summary>The published timetable. CAN BE BADLY WRONG — see <see cref="RunWindows"/>.</summary>
    public TimeOnly? AmScheduled { get; init; }

    public TimeOnly? PmScheduled { get; init; }

    public string? StopAddress { get; init; }

    public GeoPoint? Stop { get; init; }

    /// <summary>
    /// Every scan known, oldest first. Accumulated locally, because the endpoint
    /// only ever returns the current day.
    /// </summary>
    public IReadOnlyList<ScanEvent> Scans { get; init; } = [];

    public TimeOnly? ScheduledFor(Run run) => run == Run.Am ? AmScheduled : PmScheduled;

    public ScanEvent? LastScan => Scans.Count > 0 ? Scans[^1] : null;

    public ScanEvent? LastScanOf(ScanKind kind)
    {
        for (int index = Scans.Count - 1; index >= 0; index--)
        {
            if (Scans[index].Kind == kind)
            {
                return Scans[index];
            }
        }

        return null;
    }
}
