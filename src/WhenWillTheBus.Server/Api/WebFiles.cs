// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Frozen;
using System.Reflection;

namespace WhenWillTheBus.Server.Api;

/// <summary>
/// The /bus page's files, served out of the assembly.
/// </summary>
/// <remarks>
/// A FIXED LIST, not a folder. Only these names are ever answered, so no path
/// a browser sends can reach anything else in the assembly or on disk.
/// </remarks>
public static class WebFiles
{
    private static readonly FrozenDictionary<string, (string Resource, string ContentType)> Served =
        new Dictionary<string, (string, string)>
        {
            [""] = ("Web/bus.html", "text/html; charset=utf-8"),
            ["bus.js"] = ("Web/bus.js", "text/javascript; charset=utf-8"),
            ["bus.css"] = ("Web/bus.css", "text/css; charset=utf-8"),
            ["leaflet.js"] = ("Web/leaflet/leaflet.js", "text/javascript; charset=utf-8"),
            ["leaflet.css"] = ("Web/leaflet/leaflet.css", "text/css; charset=utf-8"),
        }.ToFrozenDictionary();

    /// <summary>
    /// What the page may load, and from where: itself, and map tiles.
    /// </summary>
    /// <remarks>
    /// The tiles are the one thing fetched from outside, and they are images.
    /// Nothing else -- no script, font or stylesheet from anywhere but here --
    /// so a page that knows where a child is cannot be made to tell anyone.
    /// Inline styles are allowed because Leaflet positions every tile with one.
    /// </remarks>
    public const string Policy =
        "default-src 'self'; img-src 'self' data: https://tile.openstreetmap.org; "
        + "style-src 'self' 'unsafe-inline'; script-src 'self'; connect-src 'self'; "
        + "frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    public static IResult Serve(string name)
    {
        if (!Served.TryGetValue(name, out (string Resource, string ContentType) file))
        {
            return Results.NotFound();
        }

        Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(file.Resource);
        return stream is null ? Results.NotFound() : Results.Stream(stream, file.ContentType);
    }
}
