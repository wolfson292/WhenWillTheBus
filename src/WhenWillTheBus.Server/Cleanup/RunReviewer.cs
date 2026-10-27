// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Options;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Prediction;
using WhenWillTheBus.Core.Storage;

namespace WhenWillTheBus.Server.Cleanup;

/// <summary>
/// Has Claude say what a finished run was: the usual journey, a detour, a
/// substitute bus, or GPS that cannot be trusted.
/// </summary>
/// <remarks>
/// JUDGEMENT, NOT GEOMETRY. Road matching already put the line on the roads;
/// what it cannot do is notice that a run which matched with high confidence
/// was half a mile shorter and forty minutes late, or that a frozen GPS
/// produced a confident match to a road the bus never used. Those are the runs
/// that draw lies on the map, and the review is what leaves them out.
///
/// SENT AS A SUMMARY. <see cref="RunSummary"/> is times, distances and counts;
/// no coordinate, address or name leaves the house.
/// </remarks>
public sealed class RunReviewer(IOptions<CleanupOptions> options, ILogger<RunReviewer> logger)
{
    private const string Instructions = """
        You review one finished school-bus run for a family's bus-tracking app, from a summary
        of the run compared with the other runs of the same kind. Decide which of these it was:

        - usual: the bus did what it normally does. Small differences in time or distance are
          normal: traffic, a minute or two either way, a slightly different start of recording.
        - detour: a real journey that clearly left the usual roads for part of the way, such as a
          low share on usual roads with otherwise healthy GPS, or a distance well off the usual.
        - substitute: reported as a substitute bus, or the pattern only makes sense as a
          different vehicle or route covering the run.
        - bad_gps: the recording cannot be trusted as a route: very few live positions, long gaps
          between them, a very low road-match confidence, or a distance far too short to be the
          journey. Polls with no live position are not part of the route and are normal; they
          only matter if the route itself is also thin.

        Prefer usual when nothing stands out. Use bad_gps only when the recording itself is the
        problem, not merely the journey being unusual.

        The note is one short, plain sentence for a parent, saying what was different (or that
        nothing was), with no jargon such as "fixes", "confidence" or "matching".
        """;

    public bool Enabled => !string.IsNullOrWhiteSpace(options.Value.AnthropicApiKey);

    private AnthropicClient? _client;

    /// <summary>The review, or null where none came back that can be used.</summary>
    public async Task<RunReview?> ReviewAsync(RunSummary summary, CancellationToken token)
    {
        _client ??= new AnthropicClient { ApiKey = options.Value.AnthropicApiKey };

        BetaMessage response = await _client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = options.Value.Model,
            MaxTokens = 2048,

            // A short classification: low effort is plenty, and keeps a term's
            // backfill to pennies.
            OutputConfig = new BetaOutputConfig
            {
                Effort = Effort.Low,
                Format = new BetaJsonOutputFormat { Schema = Schema },
            },

            // A policy decline is re-served by another model inside the same
            // call, rather than leaving the run unreviewed.
            Betas = ["server-side-fallback-2026-06-01"],
            Fallbacks = new List<BetaFallbackParam> { new() { Model = "claude-opus-4-8" } },

            System = Instructions,
            Messages = [new BetaUserMessageParam { Content = summary.Describe() }],
        }, token).ConfigureAwait(false);

        if (response.StopReason == "refusal")
        {
            logger.LogWarning("Run review declined ({Category})", response.StopDetails?.Category);
            return null;
        }

        string? json = response.Content
            .Select(block => block.Value)
            .OfType<BetaTextBlock>()
            .Select(block => block.Text)
            .FirstOrDefault();

        return Parse(json);
    }

    /// <summary>The structured answer, or null if it is not one.</summary>
    public static RunReview? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            string? verdict = root.TryGetProperty("verdict", out JsonElement value) ? value.GetString() : null;
            string note = root.TryGetProperty("note", out JsonElement text) ? text.GetString() ?? string.Empty : string.Empty;

            return CleanupFields.Parse(verdict) is RunVerdict parsed
                ? new RunReview(parsed, note.Length > 200 ? note[..200] : note)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly Dictionary<string, JsonElement> Schema = new()
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["properties"] = JsonSerializer.SerializeToElement(new
        {
            verdict = new { type = "string", @enum = new[] { "usual", "detour", "substitute", "bad_gps" } },
            note = new { type = "string" },
        }),
        ["required"] = JsonSerializer.SerializeToElement(new[] { "verdict", "note" }),
        ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
    };
}
