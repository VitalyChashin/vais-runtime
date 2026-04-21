// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Core.Guardrails;

/// <summary>
/// Input guardrail that denies when the most recent user turn exceeds a
/// configurable character count. Trivial first-line-of-defence guard;
/// composes with richer content filters downstream.
/// </summary>
/// <remarks>
/// Manifest shape:
/// <code>
/// guardrails:
///   input:
///     - name: LengthCap
///       params: { maxChars: 2000 }
/// </code>
/// </remarks>
public sealed class LengthCapInputGuardrail : IInputGuardrail
{
    private readonly int _maxChars;

    /// <summary>Construct with the configured character cap.</summary>
    public LengthCapInputGuardrail(int maxChars)
    {
        if (maxChars <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxChars), maxChars, "maxChars must be positive.");
        }
        _maxChars = maxChars;
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

        if (latestUser.Length > _maxChars)
        {
            return new ValueTask<GuardrailOutcome>(GuardrailOutcome.Deny(
                $"User input is {latestUser.Length} characters, which exceeds the {_maxChars}-character cap."));
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
