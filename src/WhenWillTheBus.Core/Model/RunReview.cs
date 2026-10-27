// SPDX-License-Identifier: GPL-3.0-or-later

namespace WhenWillTheBus.Core.Model;

/// <summary>What a run turned out to be, judged after the fact.</summary>
public enum RunVerdict
{
    /// <summary>The bus did what it does: the usual roads, at about the usual time.</summary>
    Usual,

    /// <summary>A real journey that left the usual roads for part of the way.</summary>
    Detour,

    /// <summary>Driven by another vehicle, or behaving as if it was.</summary>
    Substitute,

    /// <summary>The fixes cannot be trusted: frozen, jumping, or too sparse to be a route.</summary>
    BadGps,
}

/// <summary>
/// One run, reviewed: what it was, in a sentence a parent would read.
/// </summary>
/// <remarks>
/// Written by the worker once a run is over, from a SUMMARY of it -- times,
/// distances, how much of it was on the usual roads -- never from its
/// coordinates. The map uses <see cref="Verdict"/> to leave out runs whose GPS
/// was garbage, so one frozen morning cannot draw a road the bus never took.
/// </remarks>
public sealed record RunReview(RunVerdict Verdict, string Note);
