// SPDX-License-Identifier: GPL-3.0-or-later

using System.Web;

namespace WhenWillTheBus.App.Services;

/// <summary>
/// Configuring a phone by tapping a link.
/// </summary>
/// <remarks>
/// A family member should not have to type a worker address and a forty
/// character key on a phone keyboard. One link does it.
///
/// NOTHING IS SAVED WITHOUT ASKING. A custom URL scheme can be opened by any app
/// or any web page, so an arriving link is a suggestion and never an
/// instruction: the app shows where it points and who it claims to be, and waits
/// to be told yes.
/// </remarks>
public static class SetupLink
{
    public const string Scheme = "whenwillthebus";

    public readonly record struct Details(string Url, string Key);

    /// <summary>Raised when a setup link arrives, for the UI to confirm.</summary>
    public static event Action<Details>? Received;

    public static void Offer(Details details) => Received?.Invoke(details);

    /// <summary>Build a link. The key travels in it, so treat it like the key.</summary>
    public static string Build(string url, string key) =>
        $"{Scheme}://setup?url={Uri.EscapeDataString(url)}&key={Uri.EscapeDataString(key)}";

    public static bool TryParse(string? link, out Details details)
    {
        details = default;

        if (string.IsNullOrWhiteSpace(link)
            || !Uri.TryCreate(link, UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        System.Collections.Specialized.NameValueCollection query = HttpUtility.ParseQueryString(uri.Query);
        string? url = query["url"]?.Trim();
        string? key = query["key"];

        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        // Only http(s). A scheme handler is reachable from a web page, and a
        // link that could point the app at something other than a web address
        // is a way to make it do something it was never asked to.
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? target)
            || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        details = new Details(url.TrimEnd('/'), key);
        return true;
    }
}
