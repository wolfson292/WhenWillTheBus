// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Text;
using UIKit;

namespace WhenWillTheBus.App.Services;

/// <summary>
/// What this phone may tell the worker about itself.
/// </summary>
/// <remarks>
/// APPLE DECIDES WHAT GOES IN HERE, not convenience. The rules that shaped it:
///
/// <list type="bullet">
/// <item><c>identifierForVendor</c> is the identifier an app is permitted to
/// use for this. It is scoped to the vendor, not the hardware, and iOS reissues
/// it once the last app from this vendor leaves the device — so a reinstalled
/// phone legitimately appears as a new one, and that is the point of it.</item>
///
/// <item><c>UIDevice.name</c> has returned a GENERIC MODEL NAME since iOS 16
/// unless the app holds an entitlement Apple grants for managed fleets, which
/// this is not. Asking for it would return "iPhone" for every phone in the
/// house, so the friendly name is typed by its owner instead.</item>
///
/// <item><c>hw.machine</c> is a MODEL — every unit of that product reports the
/// same string. It identifies a product, not a person, and is not among the
/// APIs Apple requires a declared reason to call.</item>
/// </list>
///
/// Deliberately absent, and it should stay that way: the advertising
/// identifier, the UDID or serial number, the MAC address, the phone number,
/// any account, and any location. The worker already knows where a child is.
/// </remarks>
public static partial class DeviceIdentity
{
    /// <summary>Where the owner's chosen name for this phone is kept.</summary>
    private const string LabelKey = "wwtb.device.label";

    /// <summary>
    /// The name shown on the worker's page, or null if never set.
    /// </summary>
    /// <remarks>
    /// Plain preferences rather than the keychain: this is a label somebody
    /// chose for a phone, and treating it as a secret would only mean it did
    /// not survive being read on a locked device.
    /// </remarks>
    public static string? Label
    {
        get => Preferences.Get(LabelKey, null as string);
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                Preferences.Remove(LabelKey);
            }
            else
            {
                Preferences.Set(LabelKey, value.Trim());
            }
        }
    }

    /// <summary>
    /// A UUID iOS issues for this vendor on this device.
    /// </summary>
    /// <remarks>
    /// Null is a real possibility, not defensive noise: iOS returns nothing
    /// before the device has been unlocked once after a restart. A caller that
    /// assumed otherwise would register a phone under the string "null".
    /// </remarks>
    public static string? VendorId => UIDevice.CurrentDevice.IdentifierForVendor?.AsString();

    /// <summary>The product, e.g. "iPhone17,1" — shared by every unit of it.</summary>
    public static string? Model
    {
        get
        {
            try
            {
                nint size = 0;
                if (sysctlbyname("hw.machine", IntPtr.Zero, ref size, IntPtr.Zero, 0) != 0 || size <= 0)
                {
                    return UIDevice.CurrentDevice.Model;
                }

                IntPtr buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    return sysctlbyname("hw.machine", buffer, ref size, IntPtr.Zero, 0) == 0
                        ? Marshal.PtrToStringAnsi(buffer)
                        : UIDevice.CurrentDevice.Model;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch (Exception)
            {
                // A model string is a nicety. Never let it take down the poll
                // that feeds the thing the app is actually for.
                return UIDevice.CurrentDevice.Model;
            }
        }
    }

    public static string SystemVersion => UIDevice.CurrentDevice.SystemVersion;

    public static string AppVersion => AppInfo.Current.VersionString;

    public static string Build => AppInfo.Current.BuildString;

    /// <summary>Which APNs environment this build's push tokens belong to.</summary>
    public static string Environment =>
#if APNS_PRODUCTION
        "production";
#else
        "sandbox";
#endif

    /// <summary>
    /// The introduction, or null when there is no identifier to send under.
    /// </summary>
    public static string? HelloJson()
    {
        if (VendorId is not string id)
        {
            return null;
        }

        StringBuilder json = new();
        json.Append("{\"id\":").Append(Quote(id));
        Field(json, "label", Label);
        Field(json, "model", Model);
        Field(json, "systemVersion", SystemVersion);
        Field(json, "appVersion", AppVersion);
        Field(json, "build", Build);
        Field(json, "environment", Environment);

        // Null until notification permission has been granted, and the worker
        // treats null as "did not say" rather than "has none" -- so a hello
        // sent before the prompt is answered does not clear a token that
        // arrived on an earlier run.
        Field(json, "deviceToken", PushRegistrar.DeviceToken);
        json.Append('}');
        return json.ToString();
    }

    private static void Field(StringBuilder json, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            json.Append(",\"").Append(name).Append("\":").Append(Quote(value));
        }
    }

    /// <summary>
    /// Written by hand, and therefore escaped by hand.
    /// </summary>
    /// <remarks>
    /// A label is typed by a person and can contain a quote or a backslash, and
    /// an unescaped one would produce a body the worker rejects as malformed —
    /// presenting as a phone that simply never appears on the page.
    /// </remarks>
    private static string Quote(string value)
    {
        StringBuilder quoted = new("\"");
        foreach (char character in value)
        {
            _ = character switch
            {
                '"' => quoted.Append("\\\""),
                '\\' => quoted.Append("\\\\"),
                '\n' => quoted.Append("\\n"),
                '\r' => quoted.Append("\\r"),
                '\t' => quoted.Append("\\t"),
                < ' ' => quoted.Append(Escaped(character)),
                _ => quoted.Append(character),
            };
        }

        return quoted.Append('"').ToString();
    }

    private static string Escaped(char character) =>
        "\\u" + ((int)character).ToString("x4", System.Globalization.CultureInfo.InvariantCulture);

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int sysctlbyname(string name, IntPtr output, ref nint size, IntPtr input, nint length);
}
