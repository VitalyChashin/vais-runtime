// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Vais.Agents.Eval.Assertions;

/// <summary>
/// Passes when the number of LLM call retries during the case run does not exceed
/// the configured threshold. Wire as <c>{ "kind": "max-retries", "max": 2 }</c>.
/// A value of 0 means no retries are allowed (the default, strictest).
/// Optional <c>concept</c> filter (e.g. <c>"LlmCallRetried"</c>) scopes the check to a concept subtree.
/// </summary>
internal sealed class MaxRetriesAssertion : IEvalAssertion
{
    private readonly int _max;
    private readonly string? _conceptFilter;
    private readonly IFailureOntologyCatalog? _catalog;

    internal MaxRetriesAssertion(int max, string? conceptFilter = null, IFailureOntologyCatalog? catalog = null)
    {
        _max = max;
        _conceptFilter = conceptFilter;
        _catalog = catalog;
    }

    /// <inheritdoc/>
    public string Kind => "max-retries";

    /// <inheritdoc/>
    public ValueTask<EvalAssertionResult> EvaluateAsync(EvalCaseContext ctx, EvalRunRecord run, CancellationToken ct)
    {
        if (!MatchesConcept(RunHealthSignalKind.LlmRetry))
            return ValueTask.FromResult(new EvalAssertionResult(EvalAssertionStatus.Pass, Score: 1.0, Reason: null));

        var retries = run.Events.OfType<LlmCallRetried>().Count();
        if (retries <= _max)
            return ValueTask.FromResult(new EvalAssertionResult(EvalAssertionStatus.Pass, Score: 1.0, Reason: null));

        return ValueTask.FromResult(new EvalAssertionResult(
            EvalAssertionStatus.Fail,
            Score: 0.0,
            Reason: $"LLM was retried {retries} time(s); threshold is {_max}."));
    }

    private bool MatchesConcept(RunHealthSignalKind kind)
    {
        if (_conceptFilter is null) return true;
        if (_catalog is null) return true;
        var concept = _catalog.FromSignalKind(kind);
        return concept is not null && _catalog.IsMatchOrDescendant(concept.Name, _conceptFilter);
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
        string? conceptFilter = null;
        if (args.ValueKind == JsonValueKind.Object)
        {
            if (args.TryGetProperty("max", out var prop))
                max = prop.GetInt32();
            if (args.TryGetProperty("concept", out var cp))
                conceptFilter = cp.GetString();
        }
        else if (args.ValueKind == JsonValueKind.Number)
        {
            max = args.GetInt32();
        }
        var catalog = services.GetService<IFailureOntologyCatalog>();
        return new MaxRetriesAssertion(max, conceptFilter, catalog);
    }
}
