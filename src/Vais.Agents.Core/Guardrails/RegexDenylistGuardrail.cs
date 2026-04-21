// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.RegularExpressions;

namespace Vais.Agents.Core.Guardrails;

/// <summary>
/// Input guardrail that denies when the most recent user turn matches a
/// configured regex. Use for PII-detection, blocked-phrase lists,
/// credit-card / social-security-number shapes, etc.
/// </summary>
public sealed class RegexDenylistInputGuardrail : IInputGuardrail
{
    private readonly Regex _pattern;

    /// <summary>Construct with the denylist regex.</summary>
    public RegexDenylistInputGuardrail(Regex pattern)
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

        if (_pattern.IsMatch(latestUser))
        {
            return new ValueTask<GuardrailOutcome>(GuardrailOutcome.Deny(
                $"User input matches denylist pattern '{_pattern}'."));
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
/// Output guardrail that denies when the assistant response matches a
/// configured regex. Use for post-hoc PII scrubbing, blocked-response-phrase
/// lists, jail-break-output shapes, etc.
/// </summary>
public sealed class RegexDenylistOutputGuardrail : IOutputGuardrail
{
    private readonly Regex _pattern;

    /// <summary>Construct with the denylist regex.</summary>
    public RegexDenylistOutputGuardrail(Regex pattern)
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

        if (_pattern.IsMatch(response.Text))
        {
            return new ValueTask<GuardrailOutcome>(GuardrailOutcome.Deny(
                $"Assistant response matches denylist pattern '{_pattern}'."));
        }

        return new ValueTask<GuardrailOutcome>(GuardrailOutcome.Pass);
    }
}
