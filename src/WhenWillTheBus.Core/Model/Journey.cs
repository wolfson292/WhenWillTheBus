// SPDX-License-Identifier: GPL-3.0-or-later

namespace WhenWillTheBus.Core.Model;

/// <summary>Which stage of the run a rider is in, and how far through it.</summary>
public sealed record Journey
{
    public required JourneyStage Stage { get; init; }

    /// <summary>0-100 through whatever this stage measures, or null when nothing is meaningful.</summary>
    public int? Progress { get; init; }

    /// <summary>
    /// The instant being counted towards: school on the way there, the home stop
    /// on the way back. Rounded to the minute it will be DISPLAYED as.
    /// </summary>
    public DateTimeOffset? Target { get; init; }

    /// <summary>When this journey began, for a bar that fills against elapsed time.</summary>
    public DateTimeOffset? Boarded { get; init; }

    /// <summary>
    /// Identifies this journey, e.g. "20260914-am", so a notification carrying
    /// it never updates yesterday's. Use it as the Live Activity identity.
    /// </summary>
    public string? JourneyId { get; init; }

    /// <summary>Whether anything is happening worth showing.</summary>
    public bool Active => Stage != JourneyStage.Idle;

    public static Journey Idle { get; } = new() { Stage = JourneyStage.Idle };
}
