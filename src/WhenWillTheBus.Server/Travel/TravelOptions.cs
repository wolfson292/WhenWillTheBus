// SPDX-License-Identifier: GPL-3.0-or-later

namespace WhenWillTheBus.Server.Travel;

/// <summary>Drive times, for "can I get to the stop before the bus?".</summary>
public sealed class TravelOptions
{
    public const string Section = "Travel";

    /// <summary>
    /// Live traffic, asked for one trip at the moment it is asked about and
    /// never otherwise. A SECRET: WWTB_Travel__TomTomApiKey in the stack.
    /// Unset, drive times are empty-road times from <see cref="RoadsUrl"/>.
    /// </summary>
    public string? TomTomApiKey { get; set; }

    /// <summary>The household's OSRM, for empty-road times and as the fallback.</summary>
    public string? RoadsUrl { get; set; }

    /// <summary>How early to be at the stop before the bus is due.</summary>
    public TimeSpan Margin { get; set; } = TimeSpan.FromMinutes(2);
}
