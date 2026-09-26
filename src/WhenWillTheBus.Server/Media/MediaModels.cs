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
    string Outcome);

/// <summary>What came of trying to add something.</summary>
/// <param name="Outcome">
/// Plain words, because this reaches a phone screen. "Added", "Already in the
/// library", or what went wrong.
/// </param>
public sealed record AddOutcome(bool Added, string Outcome);
