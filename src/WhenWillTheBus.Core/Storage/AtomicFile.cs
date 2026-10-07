// SPDX-License-Identifier: GPL-3.0-or-later

namespace WhenWillTheBus.Core.Storage;

/// <summary>Replace a file whole, so nothing can ever read half of one.</summary>
/// <remarks>
/// Written beside the target and moved into place, so a process killed
/// mid-write leaves the old file rather than a torn new one.
///
/// EVERY WRITE GETS ITS OWN TEMPORARY FILE. They all used to share
/// "&lt;file&gt;.tmp", and two saves at once -- a phone sending two hellos in the
/// same second, on 7 Oct -- both wrote it, the first moved it into place, and
/// the second's move found nothing there and failed the request with a 500.
/// </remarks>
public static class AtomicFile
{
    public static async Task WriteAsync(string path, byte[] contents, CancellationToken token = default)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, contents, token).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            // Only there if the write or the move failed; never leave it behind.
            File.Delete(temporary);
        }
    }
}
