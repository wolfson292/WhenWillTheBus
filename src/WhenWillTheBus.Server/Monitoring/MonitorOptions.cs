// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel.DataAnnotations;

namespace WhenWillTheBus.Server.Monitoring;

/// <summary>How the worker talks to WheresTheBus, and where it keeps what it learns.</summary>
/// <remarks>
/// THE PASSWORD IS A SECRET and belongs in an environment variable or a Docker
/// secret, never in appsettings.json in a repository. This worker also knows a
/// child's live location: treat the machine it runs on accordingly.
/// </remarks>
public sealed class MonitorOptions
{
    public const string Section = "WheresTheBus";

    [Required]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// A stable per-install identifier you invent, sent as <c>imeiNo</c>. Generate
    /// one once and keep it: changing it makes every start look like a new device.
    /// </summary>
    [Required]
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>The rider's timezone. Every window and run is a local-clock question.</summary>
    public string TimeZone { get; set; } = "America/New_York";

    /// <summary>Where learned history and activity registrations live. Mount this as a volume.</summary>
    public string DataDirectory { get; set; } = "/data";

    /// <summary>
    /// While a run is being watched. The API advertises 15 seconds; 30 keeps the
    /// marker useful at half the request rate against somebody else's service.
    /// </summary>
    public TimeSpan BusPoll { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Outside every run's approach window there is nothing to predict, so the
    /// worker stops hammering a service that is running for schoolchildren.
    /// </summary>
    public TimeSpan IdlePoll { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Known to be too slow around afternoon loading — see the handoff's open work.</summary>
    public TimeSpan ScanPoll { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The roster changes a handful of times a year.</summary>
    public TimeSpan RosterRefresh { get; set; } = TimeSpan.FromHours(6);

    public string HistoryPath => Path.Combine(DataDirectory, "history.json");

    public string RegistryPath => Path.Combine(DataDirectory, "activities.json");

    /// <summary>The phones that have introduced themselves, for the status page.</summary>
    public string ClientsPath => Path.Combine(DataDirectory, "clients.json");
}
