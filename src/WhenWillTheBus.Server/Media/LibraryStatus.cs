// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;

namespace WhenWillTheBus.Server.Media;

/// <summary>What one request's title looks like in the library right now.</summary>
/// <param name="Asked">False when the instance could not be reached, so nothing is known.</param>
/// <param name="Entry">Null when it was asked and does not have the title.</param>
public sealed record TitleStatus(bool Asked, LibraryEntry? Entry);

/// <summary>
/// The library's view of every requested title, for the status page.
/// </summary>
/// <remarks>
/// CACHED FOR A FEW MINUTES. The page reloads itself every thirty seconds and
/// lists every request ever made, so asking Radarr and Sonarr afresh each time
/// would be a hundred lookups a minute for an answer that changes when a
/// download finishes. Bounded in time too, so an instance that has gone away
/// costs the page a few seconds, not a hang -- titles it could not answer for
/// say so.
/// </remarks>
public sealed class LibraryStatus(ArrClient arr)
{
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(8);
    private const int AtOnce = 6;

    private readonly ConcurrentDictionary<(MediaKind, long), (TitleStatus Status, DateTimeOffset At)> _known = [];

    public async Task<IReadOnlyDictionary<(MediaKind, long), TitleStatus>> ForAsync(
        IEnumerable<MediaRequest> requests,
        DateTimeOffset now,
        CancellationToken token)
    {
        List<(MediaKind, long)> titles = requests.Select(request => (request.Kind, request.RemoteId)).Distinct().ToList();

        List<(MediaKind, long)> stale = titles
            .Where(title => !_known.TryGetValue(title, out var seen) || now - seen.At > FreshFor)
            .ToList();

        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(Budget);

        try
        {
            await Parallel.ForEachAsync(
                stale,
                new ParallelOptions { MaxDegreeOfParallelism = AtOnce, CancellationToken = budget.Token },
                async (title, cancel) =>
                {
                    (LibraryEntry? Entry, bool Asked)? found =
                        await arr.LookupAsync(title.Item1, title.Item2, cancel).ConfigureAwait(false);

                    // Only an answer is remembered. "Could not ask" is retried
                    // on the next load rather than held for minutes.
                    if (found is not null)
                    {
                        _known[title] = (new TitleStatus(true, found.Value.Entry), now);
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // Out of time. What was answered is shown; the rest says so.
        }

        return titles.ToDictionary(
            title => title,
            title => _known.TryGetValue(title, out var seen) ? seen.Status : new TitleStatus(false, null));
    }
}
