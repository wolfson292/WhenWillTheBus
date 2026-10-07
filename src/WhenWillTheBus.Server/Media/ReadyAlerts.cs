// SPDX-License-Identifier: GPL-3.0-or-later

namespace WhenWillTheBus.Server.Media;

/// <summary>Which "it's ready" message a request has earned, if any.</summary>
public enum ReadyStep
{
    None,

    /// <summary>A series' first episodes have arrived. Said once.</summary>
    Started,

    /// <summary>All of it is there. Said once, and the request is then done.</summary>
    Ready,
}

/// <summary>
/// Decides when to tell somebody that what they asked for can be watched.
/// </summary>
/// <remarks>
/// A film is one message. A series is at most two -- when the first episodes
/// land, and when it has caught up -- because a season downloads over hours
/// and "one more episode" every few minutes is a phone nobody wants. A series
/// that is already complete the first time it is looked at gets one message.
/// Pure, so every case is a test rather than an evening of waiting.
/// </remarks>
public static class ReadyAlerts
{
    public static ReadyStep Next(MediaRequest request, Readiness readiness)
    {
        if (request.ReadyAt is not null || !readiness.Any)
        {
            return ReadyStep.None;
        }

        if (readiness.Complete)
        {
            return ReadyStep.Ready;
        }

        return request.Kind is MediaKind.Series && request.StartedAt is null
            ? ReadyStep.Started
            : ReadyStep.None;
    }

    public static (string Title, string Body) TextFor(ReadyStep step, MediaRequest request, Readiness readiness)
    {
        string name = request.Year is int year ? $"{request.Title} ({year})" : request.Title;

        // Said when somebody else asked for it on their behalf: otherwise a
        // film turning up that you never asked for is a puzzle, not a present.
        string from = request.ForId is not null && request.RequestedBy is string who ? $" {who} asked for it for you." : string.Empty;

        if (step is ReadyStep.Started)
        {
            string some = readiness.Files == 1 ? "The first episode" : $"The first {readiness.Files} episodes";
            return ("Starting to arrive", $"{some} of {name} can be watched now.{from}");
        }

        return request.Kind is MediaKind.Series && request.StartedAt is not null
            ? ("Ready to watch", $"All of {name} is ready to watch.{from}")
            : ("Ready to watch", $"{name} is ready to watch.{from}");
    }
}
