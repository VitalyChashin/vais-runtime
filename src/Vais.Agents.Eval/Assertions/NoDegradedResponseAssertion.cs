// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Eval.Assertions;

/// <summary>
/// Passes when no turn completed with a degraded/partial result during the case run.
/// Catches the L3 plugin-fallback pattern: a turn that calls <c>is_partial=true</c> or returns a placeholder
/// ("No analysis produced.") emits a <see cref="TurnCompleted"/> with <c>Level=Warning</c> rather than a
/// <see cref="TurnFailed"/> — so <see cref="NoTurnFailedAssertion"/> would pass while the output was silently degraded.
/// No configuration params — wire as <c>{ "kind": "no-degraded-response" }</c>.
/// </summary>
internal sealed class NoDegradedResponseAssertion : IEvalAssertion
{
    /// <inheritdoc/>
    public string Kind => "no-degraded-response";

    /// <inheritdoc/>
    public ValueTask<EvalAssertionResult> EvaluateAsync(EvalCaseContext ctx, EvalRunRecord run, CancellationToken ct)
    {
        var degraded = run.Events.OfType<TurnCompleted>()
            .FirstOrDefault(e => e.Level == FailureLevel.Warning);

        if (degraded is null)
            return ValueTask.FromResult(new EvalAssertionResult(EvalAssertionStatus.Pass, Score: 1.0, Reason: null));

        var text = degraded.AssistantText;
        return ValueTask.FromResult(new EvalAssertionResult(
            EvalAssertionStatus.Fail,
            Score: 0.0,
            Reason: $"Turn produced a degraded (partial) result: '{Truncate(text, 120)}'"));
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>Factory for <see cref="NoDegradedResponseAssertion"/>.</summary>
internal sealed class NoDegradedResponseAssertionFactory : IEvalAssertionFactory
{
    /// <inheritdoc/>
    public string Kind => "no-degraded-response";

    /// <inheritdoc/>
    public IEvalAssertion Create(JsonElement args, IServiceProvider services) => new NoDegradedResponseAssertion();
}
