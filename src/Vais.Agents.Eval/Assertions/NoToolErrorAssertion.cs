// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Eval.Assertions;

/// <summary>
/// Passes when no tool invocation failed during the case run.
/// Catches recovered tool errors that <see cref="NoTurnFailedAssertion"/> misses — a tool error
/// that the model reads and recovers from does not produce a <see cref="TurnFailed"/> event, but
/// does produce a <see cref="ToolCallCompleted"/> with <c>Succeeded=false</c>.
/// No configuration params — wire as <c>{ "kind": "no-tool-error" }</c>.
/// </summary>
internal sealed class NoToolErrorAssertion : IEvalAssertion
{
    /// <inheritdoc/>
    public string Kind => "no-tool-error";

    /// <inheritdoc/>
    public ValueTask<EvalAssertionResult> EvaluateAsync(EvalCaseContext ctx, EvalRunRecord run, CancellationToken ct)
    {
        var failed = run.Events.OfType<ToolCallCompleted>().FirstOrDefault(e => !e.Succeeded);
        if (failed is null)
            return ValueTask.FromResult(new EvalAssertionResult(EvalAssertionStatus.Pass, Score: 1.0, Reason: null));

        return ValueTask.FromResult(new EvalAssertionResult(
            EvalAssertionStatus.Fail,
            Score: 0.0,
            Reason: $"Tool '{failed.ToolName}' failed: {failed.Error ?? "unknown error"}"));
    }
}

/// <summary>Factory for <see cref="NoToolErrorAssertion"/>.</summary>
internal sealed class NoToolErrorAssertionFactory : IEvalAssertionFactory
{
    /// <inheritdoc/>
    public string Kind => "no-tool-error";

    /// <inheritdoc/>
    public IEvalAssertion Create(JsonElement args, IServiceProvider services) => new NoToolErrorAssertion();
}
