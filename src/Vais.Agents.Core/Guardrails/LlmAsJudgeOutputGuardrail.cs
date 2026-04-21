// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Globalization;
using System.Text.RegularExpressions;

namespace Vais.Agents.Core.Guardrails;

/// <summary>
/// Output guardrail that uses a separate judge model to score the assistant
/// response on a 0–1 scale and denies when the score falls below a
/// configurable threshold. The judge prompt is sent as the system prompt;
/// the response body carries a placeholder <c>{{response}}</c> that the
/// guardrail substitutes with the actual assistant text before invoking
/// the judge.
/// </summary>
/// <remarks>
/// <para>
/// The guardrail parses the first decimal number (0.0 – 1.0) out of the
/// judge's response text. Judges that emit richer JSON should use a
/// schema-constrained model (via <c>ModelSpec.ResponseFormat</c>) so the
/// score shows up on its own line — the default parser is forgiving but
/// not structured.
/// </para>
/// <para>
/// The <c>ICompletionProvider</c> for the judge is supplied at construction
/// time — callers typically build it via an <c>ICompletionProviderPool</c>
/// so multiple judges sharing the same <c>ModelSpec</c> don't each
/// instantiate their own SDK client.
/// </para>
/// </remarks>
public sealed class LlmAsJudgeOutputGuardrail : IOutputGuardrail
{
    private static readonly Regex ScoreRegex = new(@"\b(0(?:\.\d+)?|1(?:\.0+)?)\b", RegexOptions.Compiled);

    private readonly ICompletionProvider _judge;
    private readonly string _judgePrompt;
    private readonly double _minScore;

    /// <summary>Construct with the judge provider + prompt + threshold.</summary>
    /// <param name="judge">Completion provider backing the judge model.</param>
    /// <param name="judgePrompt">System prompt for the judge. Must include <c>{{response}}</c> — the guardrail substitutes the assistant response at invoke time.</param>
    /// <param name="minScore">Minimum acceptable score in [0, 1]. Responses below this threshold are denied.</param>
    public LlmAsJudgeOutputGuardrail(ICompletionProvider judge, string judgePrompt, double minScore)
    {
        ArgumentNullException.ThrowIfNull(judge);
        ArgumentException.ThrowIfNullOrWhiteSpace(judgePrompt);
        if (minScore < 0 || minScore > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minScore), minScore, "minScore must be in [0, 1].");
        }

        _judge = judge;
        _judgePrompt = judgePrompt;
        _minScore = minScore;
    }

    /// <inheritdoc />
    public async ValueTask<GuardrailOutcome> EvaluateAsync(
        CompletionResponse response,
        AgentContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);

        var renderedPrompt = _judgePrompt.Replace("{{response}}", response.Text, StringComparison.Ordinal);

        var judgeRequest = new CompletionRequest(
            History: new[] { new ChatTurn(AgentChatRole.User, "Score this response per the system prompt's rubric. Reply with just the decimal score.") },
            SystemPrompt: renderedPrompt);

        var judgeResponse = await _judge.CompleteAsync(judgeRequest, cancellationToken).ConfigureAwait(false);

        if (!TryParseScore(judgeResponse.Text, out var score))
        {
            return GuardrailOutcome.Deny(
                $"LLM-as-judge could not parse a score in [0, 1] from the judge's response: '{Truncate(judgeResponse.Text, 120)}'.");
        }

        if (score < _minScore)
        {
            return GuardrailOutcome.Deny(
                string.Format(CultureInfo.InvariantCulture,
                    "LLM-as-judge scored the response at {0:F2}, below the required {1:F2}.",
                    score, _minScore));
        }

        return GuardrailOutcome.Pass;
    }

    private static bool TryParseScore(string judgeText, out double score)
    {
        var match = ScoreRegex.Match(judgeText);
        if (!match.Success)
        {
            score = double.NaN;
            return false;
        }
        return double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out score);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
