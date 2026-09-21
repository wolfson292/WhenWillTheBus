// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.Json;

namespace WhenWillTheBus.Core.Api;

/// <summary>
/// Tolerant readers for an undocumented API.
/// </summary>
/// <remarks>
/// The service was reverse-engineered from a Flutter web client and is not
/// contractually stable. Numbers arrive sometimes as JSON numbers and sometimes
/// as strings; fields appear and disappear. Nothing here throws on a shape it
/// did not expect — a missing coordinate means "no position", which every
/// caller already has to handle, whereas an exception on a background poll
/// means the app stops predicting for a child standing at a kerb.
/// </remarks>
public static class Json
{
    public static JsonElement? Property(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
            ? value
            : null;

    public static double? Double(this JsonElement element, string name)
    {
        JsonElement? value = element.Property(name);
        return value?.ValueKind switch
        {
            JsonValueKind.Number => value.Value.GetDouble(),
            JsonValueKind.String => double.TryParse(
                value.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                    ? parsed
                    : null,
            _ => null,
        };
    }

    public static long? Long(this JsonElement element, string name)
    {
        JsonElement? value = element.Property(name);
        return value?.ValueKind switch
        {
            JsonValueKind.Number => value.Value.TryGetInt64(out long number) ? number : null,
            JsonValueKind.String => long.TryParse(
                value.Value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
                    ? parsed
                    : null,
            _ => null,
        };
    }

    public static string? String(this JsonElement element, string name)
    {
        JsonElement? value = element.Property(name);
        return value?.ValueKind switch
        {
            JsonValueKind.String => value.Value.GetString(),
            JsonValueKind.Number => value.Value.ToString(),
            _ => null,
        };
    }

    public static bool Bool(this JsonElement element, string name, bool fallback = false)
    {
        JsonElement? value = element.Property(name);
        return value?.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.Value.TryGetInt64(out long number) && number != 0,
            JsonValueKind.String => bool.TryParse(value.Value.GetString(), out bool parsed) && parsed,
            _ => fallback,
        };
    }

    public static IReadOnlyList<JsonElement> Array(this JsonElement element, string name)
    {
        JsonElement? value = element.Property(name);
        return value?.ValueKind == JsonValueKind.Array ? value.Value.EnumerateArray().ToArray() : [];
    }
}
