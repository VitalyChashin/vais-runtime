// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Globalization;
using System.Text.RegularExpressions;

namespace Vais.Agents.Core.Guardrails;

/// <summary>
/// Shared scoring kernel for LLM-as-judge patterns. Substitutes the
/// <c>{{response}}</c> placeholder in a judge prompt, invokes the judge model,
/// and regex-parses the first decimal in [0, 1] from the response.
/// </summary>
/// <remarks>
/// Both <see cref="LlmAsJudgeOutputGuardrail"/> and
/// <c>Vais.Agents.Eval.Assertions.JudgeScoreAssertion</c> delegate here so the
/// parse logic is maintained in one place.
/// </remarks>
public static class LlmJudgeScorer
{
    private static readonly Regex ScoreRegex = new(@"\b(0(?:\.\d+)?|1(?:\.0+)?)\b", RegexOptions.Compiled);

    /// <summary>
    /// Invoke <paramref name="judge"/> with the substituted prompt and parse the
    /// returned score. Returns <c>null</c> when the judge's reply contains no
    /// parseable score.
    /// </summary>
    public static async Task<double?> TryScoreAsync(
        ICompletionProvider judge,
        string judgePrompt,
        string response,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(judge);
        ArgumentException.ThrowIfNullOrWhiteSpace(judgePrompt);

        var rendered = judgePrompt.Replace("{{response}}", response, StringComparison.Ordinal);
        var request = new CompletionRequest(
            History: [new ChatTurn(AgentChatRole.User, "Score this response per the system prompt's rubric. Reply with just the decimal score.")],
            SystemPrompt: rendered);

        var judgeResponse = await judge.CompleteAsync(request, ct).ConfigureAwait(false);
        return TryParseScore(judgeResponse.Text, out var score) ? score : null;
    }

    private static bool TryParseScore(string text, out double score)
    {
        var match = ScoreRegex.Match(text);
        if (!match.Success) { score = double.NaN; return false; }
        return double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out score);
    }

    internal static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
