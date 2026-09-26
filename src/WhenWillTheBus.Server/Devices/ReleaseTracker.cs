// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.Json;

namespace WhenWillTheBus.Server.Devices;

/// <summary>
/// Which build is the current one, so a phone can be told it is behind.
/// </summary>
/// <remarks>
/// TOLD, NOT DISCOVERED. The worker could ask App Store Connect what the latest
/// build is, and that would mean putting the .p8 and the issuer id on this box
/// to answer a question the upload already knew the answer to. So the build
/// script says so when an upload succeeds, and the worker takes its word.
///
/// Build numbers here are the timestamps scripts/build-testflight.sh stamps
/// (yyyyMMddHHmm), which compare correctly as strings for any two builds of the
/// same shape — and the comparison is deliberately "different", not "less
/// than", so a rolled-back build still counts as something to install.
/// </remarks>
public sealed class ReleaseTracker(ILogger<ReleaseTracker> logger)
{
    private string? _path;

    /// <summary>The newest build uploaded, or null if none has been announced.</summary>
    public string? Latest { get; private set; }

    public DateTimeOffset? AnnouncedAt { get; private set; }

    /// <summary>Whether a phone reporting this build has something to install.</summary>
    /// <remarks>
    /// A phone that has not said which build it runs is not "behind": it is
    /// unknown, and nagging somebody on a guess is worse than staying quiet.
    /// Development builds report "1" forever, so they would otherwise be told
    /// to update several times a day.
    /// </remarks>
    public bool IsBehind(string? build) =>
        Latest is not null
        && !string.IsNullOrWhiteSpace(build)
        && build != "1"
        && !string.Equals(build, Latest, StringComparison.Ordinal);

    public async Task AnnounceAsync(string build, DateTimeOffset now, CancellationToken token = default)
    {
        Latest = build;
        AnnouncedAt = now;
        logger.LogInformation("Build {Build} announced as the current one", build);
        await SaveAsync(token).ConfigureAwait(false);
    }

    public async Task LoadAsync(string path, CancellationToken token = default)
    {
        _path = path;
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                await File.ReadAllTextAsync(path, token).ConfigureAwait(false));

            if (document.RootElement.TryGetProperty("latest", out JsonElement latest))
            {
                Latest = latest.GetString();
            }

            if (document.RootElement.TryGetProperty("announcedAt", out JsonElement at)
                && at.GetString() is string text)
            {
                AnnouncedAt = DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            }
        }
        catch (Exception error) when (error is JsonException or FormatException)
        {
            logger.LogWarning(error, "Could not read the release marker; no build is announced");
        }
    }

    private async Task SaveAsync(CancellationToken token)
    {
        if (_path is null)
        {
            return;
        }

        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("latest", Latest);
            writer.WriteString(
                "announcedAt", (AnnouncedAt ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = _path + ".tmp";
        await File.WriteAllBytesAsync(temporary, stream.ToArray(), token).ConfigureAwait(false);
        File.Move(temporary, _path, overwrite: true);
    }
}
