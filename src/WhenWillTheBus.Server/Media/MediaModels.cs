// SPDX-License-Identifier: GPL-3.0-or-later

namespace WhenWillTheBus.Server.Media;

public enum MediaKind
{
    Movie,
    Series,
}

/// <summary>
/// One thing that could be asked for.
/// </summary>
/// <param name="RemoteId">
/// TMDB for a film, TVDB for a series — whichever the instance that found it
/// keys on. Deliberately NOT our own identifier: the request has to be handed
/// back to the same instance later, and inventing a second identity for the
/// same film is how two systems end up disagreeing about what was asked for.
/// </param>
/// <param name="AlreadyHave">
/// It is in the library already. Worth showing rather than hiding, because
/// "we have this" is a useful answer to "can we watch this".
/// </param>
public sealed record MediaResult(
    MediaKind Kind,
    string Title,
    int? Year,
    long RemoteId,
    string? Overview,
    string? PosterUrl,
    bool AlreadyHave);

/// <summary>Something the family asked for, and what became of it.</summary>
public sealed record MediaRequest(
    MediaKind Kind,
    string Title,
    int? Year,
    long RemoteId,
    string? PosterUrl,
    string? RequestedBy,
    DateTimeOffset RequestedAt,
    string Outcome)
{
    /// <summary>The phone that asked, by its vendor id. Who to tell when nobody else was named.</summary>
    public string? RequestedById { get; init; }

    /// <summary>The phone it was asked for, when that is not the one that asked.</summary>
    public string? ForId { get; init; }

    /// <summary>That phone's name at the time, for the list.</summary>
    public string? RequestedFor { get; init; }

    /// <summary>A series' first episodes arrived and were announced.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>All of it arrived and was announced. Nothing more is checked after this.</summary>
    public DateTimeOffset? ReadyAt { get; init; }

    /// <summary>Who hears that it is ready: whoever it was for, else whoever asked.</summary>
    public string? RecipientId => ForId ?? RequestedById;
}

/// <summary>How much of something has actually landed in the library.</summary>
/// <param name="Files">Films: 1 or 0. Series: episode files on disk.</param>
/// <param name="Complete">Everything the instance is meant to fetch is there.</param>
public sealed record Readiness(int Files, bool Complete)
{
    public bool Any => Files > 0;
}

/// <summary>What came of trying to add something.</summary>
/// <param name="Outcome">
/// Plain words, because this reaches a phone screen. "Added", "Already in the
/// library", or what went wrong.
/// </param>
public sealed record AddOutcome(bool Added, string Outcome);

/// <summary>What the library holds for one title.</summary>
/// <param name="TitleSlug">The instance's own slug, which is what its web pages are addressed by.</param>
public sealed record LibraryEntry(Readiness Readiness, string? TitleSlug);
