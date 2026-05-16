// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Eval.Assertions;

/// <summary>
/// Passes when no <see cref="TurnFailed"/> event was emitted during the case run.
/// No configuration params — wire as <c>{ "kind": "no-turn-failed" }</c>.
/// </summary>
internal sealed class NoTurnFailedAssertion : IEvalAssertion
{
    /// <inheritdoc/>
    public string Kind => "no-turn-failed";

    /// <inheritdoc/>
    public ValueTask<EvalAssertionResult> EvaluateAsync(EvalCaseContext ctx, EvalRunRecord run, CancellationToken ct)
    {
        var failed = run.Events.OfType<TurnFailed>().FirstOrDefault();
        if (failed is null)
            return ValueTask.FromResult(new EvalAssertionResult(EvalAssertionStatus.Pass, Score: 1.0, Reason: null));

        return ValueTask.FromResult(new EvalAssertionResult(
            EvalAssertionStatus.Fail,
            Score: 0.0,
            Reason: $"Turn failed: {failed.ErrorType}: {Truncate(failed.ErrorMessage, 200)}"));
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>Factory for <see cref="NoTurnFailedAssertion"/>.</summary>
internal sealed class NoTurnFailedAssertionFactory : IEvalAssertionFactory
{
    /// <inheritdoc/>
    public string Kind => "no-turn-failed";

    /// <inheritdoc/>
    public IEvalAssertion Create(JsonElement args, IServiceProvider services) => new NoTurnFailedAssertion();
}
