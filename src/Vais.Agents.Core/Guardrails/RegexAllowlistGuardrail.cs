// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.RegularExpressions;

namespace Vais.Agents.Core.Guardrails;

/// <summary>
/// Input guardrail that denies when the most recent user turn does NOT match
/// a configured regex. Use for shapes like "only accept questions about
/// weather" or "input must be a single sentence."
/// </summary>
public sealed class RegexAllowlistInputGuardrail : IInputGuardrail
{
    private readonly Regex _pattern;

    /// <summary>Construct with the allowlist regex.</summary>
    public RegexAllowlistInputGuardrail(Regex pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        _pattern = pattern;
    }

    /// <inheritdoc />
    public ValueTask<GuardrailOutcome> EvaluateAsync(
        CompletionRequest request,
        AgentContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var latestUser = FindLatestUserText(request.History);
        if (latestUser is null)
        {
            return new ValueTask<GuardrailOutcome>(GuardrailOutcome.Pass);
        }

        if (!_pattern.IsMatch(latestUser))
        {
            return new ValueTask<GuardrailOutcome>(GuardrailOutcome.Deny(
                $"User input does not match allowlist pattern '{_pattern}'."));
        }

        return new ValueTask<GuardrailOutcome>(GuardrailOutcome.Pass);
    }

    private static string? FindLatestUserText(IReadOnlyList<ChatTurn> history)
    {
        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Role == AgentChatRole.User)
            {
                return history[i].Text;
            }
        }
        return null;
    }
}

/// <summary>
/// Output guardrail that denies when the assistant response does NOT match a
/// configured regex. Use for shapes like "response must quote a tool-supplied
/// timestamp" or "response must be valid JSON."
/// </summary>
public sealed class RegexAllowlistOutputGuardrail : IOutputGuardrail
{
    private readonly Regex _pattern;

    /// <summary>Construct with the allowlist regex.</summary>
    public RegexAllowlistOutputGuardrail(Regex pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        _pattern = pattern;
    }

    /// <inheritdoc />
    public ValueTask<GuardrailOutcome> EvaluateAsync(
        CompletionResponse response,
        AgentContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (!_pattern.IsMatch(response.Text))
        {
            return new ValueTask<GuardrailOutcome>(GuardrailOutcome.Deny(
                $"Assistant response does not match allowlist pattern '{_pattern}'."));
        }

        return new ValueTask<GuardrailOutcome>(GuardrailOutcome.Pass);
    }
}
