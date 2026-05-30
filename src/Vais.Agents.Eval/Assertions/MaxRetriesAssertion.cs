// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Eval.Assertions;

/// <summary>
/// Passes when the number of LLM call retries during the case run does not exceed
/// the configured threshold. Wire as <c>{ "kind": "max-retries", "max": 2 }</c>.
/// A value of 0 means no retries are allowed (the default, strictest).
/// </summary>
internal sealed class MaxRetriesAssertion : IEvalAssertion
{
    private readonly int _max;

    internal MaxRetriesAssertion(int max) => _max = max;

    /// <inheritdoc/>
    public string Kind => "max-retries";

    /// <inheritdoc/>
    public ValueTask<EvalAssertionResult> EvaluateAsync(EvalCaseContext ctx, EvalRunRecord run, CancellationToken ct)
    {
        var retries = run.Events.OfType<LlmCallRetried>().Count();
        if (retries <= _max)
            return ValueTask.FromResult(new EvalAssertionResult(EvalAssertionStatus.Pass, Score: 1.0, Reason: null));

        return ValueTask.FromResult(new EvalAssertionResult(
            EvalAssertionStatus.Fail,
            Score: 0.0,
            Reason: $"LLM was retried {retries} time(s); threshold is {_max}."));
    }
}

/// <summary>Factory for <see cref="MaxRetriesAssertion"/>.</summary>
internal sealed class MaxRetriesAssertionFactory : IEvalAssertionFactory
{
    /// <inheritdoc/>
    public string Kind => "max-retries";

    /// <inheritdoc/>
    public IEvalAssertion Create(JsonElement args, IServiceProvider services)
    {
        var max = 0;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("max", out var prop))
            max = prop.GetInt32();
        else if (args.ValueKind == JsonValueKind.Number)
            max = args.GetInt32();
        return new MaxRetriesAssertion(max);
    }
}
