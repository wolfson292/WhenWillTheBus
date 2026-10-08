// SPDX-License-Identifier: GPL-3.0-or-later

namespace WhenWillTheBus.Server.Media;

/// <summary>One Radarr or Sonarr instance.</summary>
/// <remarks>
/// <see cref="Url"/> MUST INCLUDE THE URL BASE if one is configured. Both of
/// these run behind a reverse proxy here and answer at /radarr and /sonarr, and
/// a URL without it returns the proxy's own 404 as HTML — which parses as
/// neither an error nor a result, and presents as search finding nothing at all.
/// </remarks>
public sealed class ArrOptions
{
    /// <summary>e.g. http://192.168.3.151:7878/radarr — not localhost: this runs in a container.</summary>
    public string Url { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Where to put it, e.g. /data/Movies. Taken from the instance when blank.</summary>
    public string? RootFolder { get; set; }

    /// <summary>
    /// Which quality profile new additions get.
    /// </summary>
    /// <remarks>
    /// Defaults to whatever the instance lists first only as a last resort.
    /// Worth setting deliberately: Ultra-HD is four to eight times the size of
    /// 1080p, and nobody notices that choice until a disk fills.
    /// </remarks>
    public int? QualityProfileId { get; set; }

    /// <summary>
    /// Where a PERSON reaches this instance, for links on the status page,
    /// e.g. https://nas.denlair.com/radarr. Optional.
    /// </summary>
    /// <remarks>
    /// <see cref="Url"/> is where the WORKER reaches it -- a LAN address with a
    /// port, which works from a browser at home and nowhere else. Left blank, a
    /// page opened through the reverse proxy links to the same site it was
    /// opened on, under <see cref="Url"/>'s path; see <see cref="ArrLinks"/>.
    /// </remarks>
    public string? LinkUrl { get; set; }

    public bool Configured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>What the family can ask for, and where it goes.</summary>
public sealed class MediaOptions
{
    public const string Section = "Media";

    public ArrOptions Radarr { get; set; } = new();

    public ArrOptions Sonarr { get; set; } = new();

    /// <summary>
    /// Whether a request starts downloading immediately.
    /// </summary>
    /// <remarks>
    /// True here by deliberate choice: the people who can reach this are the
    /// household, and making a parent tap approve on their partner's film is
    /// ceremony rather than security. Turning it off makes a request wait,
    /// which nothing in the app yet acts on — so leave it on until it does.
    /// </remarks>
    public bool AutoApprove { get; set; } = true;

    /// <summary>How many past requests to keep for the page and the app's list.</summary>
    public int HistoryLimit { get; set; } = 200;

    /// <summary>
    /// MagicMovieNight, if it is running: e.g. http://192.168.3.151:8100
    /// </summary>
    /// <remarks>
    /// Blank turns the feature off rather than producing errors. Nothing else
    /// depends on it being reachable.
    /// </remarks>
    public string? MovieNightUrl { get; set; }
}
