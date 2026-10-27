// SPDX-License-Identifier: GPL-3.0-or-later

namespace WhenWillTheBus.Server.Cleanup;

/// <summary>
/// How the worker tidies each run after it is over: matching it to roads, and
/// having it reviewed. Both are optional; either one unset simply does not happen.
/// </summary>
public sealed class CleanupOptions
{
    public const string Section = "Cleanup";

    /// <summary>
    /// The OSRM server scripts/install-roads.sh starts on the Docker host, e.g.
    /// http://192.168.3.151:5005. Unset, runs are not matched to roads.
    /// </summary>
    public string? RoadsUrl { get; set; }

    /// <summary>
    /// For the review. A SECRET: set it as WWTB_Cleanup__AnthropicApiKey in the
    /// stack, never in a file. Unset, runs are not reviewed.
    /// </summary>
    public string? AnthropicApiKey { get; set; }

    public string Model { get; set; } = "claude-opus-5-5";

    /// <summary>How many runs each pass matches, and how many it reviews.</summary>
    public int PerPass { get; set; } = 6;

    /// <summary>
    /// How often a pass runs, between bus runs only. A term's backlog clears in
    /// an hour or two; after that there are two new runs a day.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);
}
