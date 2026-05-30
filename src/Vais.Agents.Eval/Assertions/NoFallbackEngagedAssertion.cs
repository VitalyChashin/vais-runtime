// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Eval.Assertions;

/// <summary>
/// Passes when no LLM provider fallback was engaged during the case run.
/// A provider fallback (primary failed, secondary answered) is a recovered degradation — the final answer
/// may be correct but was produced by a different model than intended. This assertion catches that silent
/// quality-vs-cost tradeoff. Wire as <c>{ "kind": "no-fallback-engaged" }</c>.
/// </summary>
internal sealed class NoFallbackEngagedAssertion : IEvalAssertion
{
    /// <inheritdoc/>
    public string Kind => "no-fallback-engaged";

    /// <inheritdoc/>
    public ValueTask<EvalAssertionResult> EvaluateAsync(EvalCaseContext ctx, EvalRunRecord run, CancellationToken ct)
    {
        var fallback = run.Events.OfType<LlmFallbackEngaged>().FirstOrDefault();
        if (fallback is null)
            return ValueTask.FromResult(new EvalAssertionResult(EvalAssertionStatus.Pass, Score: 1.0, Reason: null));

        return ValueTask.FromResult(new EvalAssertionResult(
            EvalAssertionStatus.Fail,
            Score: 0.0,
            Reason: $"LLM fallback engaged: provider[{fallback.FromProviderIndex}] → provider[{fallback.ToProviderIndex}] (reason: {fallback.Reason})"));
    }
}

/// <summary>Factory for <see cref="NoFallbackEngagedAssertion"/>.</summary>
internal sealed class NoFallbackEngagedAssertionFactory : IEvalAssertionFactory
{
    /// <inheritdoc/>
    public string Kind => "no-fallback-engaged";

    /// <inheritdoc/>
    public IEvalAssertion Create(JsonElement args, IServiceProvider services) => new NoFallbackEngagedAssertion();
}
