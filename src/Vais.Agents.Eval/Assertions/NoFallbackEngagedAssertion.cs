// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Vais.Agents.Eval.Assertions;

/// <summary>
/// Passes when no LLM provider fallback was engaged during the case run.
/// A provider fallback (primary failed, secondary answered) is a recovered degradation — the final answer
/// may be correct but was produced by a different model than intended. This assertion catches that silent
/// quality-vs-cost tradeoff. Wire as <c>{ "kind": "no-fallback-engaged" }</c>.
/// Optional <c>concept</c> filter (e.g. <c>"LlmFallbackEngaged"</c>) scopes to a concept subtree.
/// </summary>
internal sealed class NoFallbackEngagedAssertion : IEvalAssertion
{
    private readonly string? _conceptFilter;
    private readonly IFailureOntologyCatalog? _catalog;

    internal NoFallbackEngagedAssertion(string? conceptFilter = null, IFailureOntologyCatalog? catalog = null)
    {
        _conceptFilter = conceptFilter;
        _catalog = catalog;
    }

    /// <inheritdoc/>
    public string Kind => "no-fallback-engaged";

    /// <inheritdoc/>
    public ValueTask<EvalAssertionResult> EvaluateAsync(EvalCaseContext ctx, EvalRunRecord run, CancellationToken ct)
    {
        if (!MatchesConcept(RunHealthSignalKind.LlmFallback))
            return ValueTask.FromResult(new EvalAssertionResult(EvalAssertionStatus.Pass, Score: 1.0, Reason: null));

        var fallback = run.Events.OfType<LlmFallbackEngaged>().FirstOrDefault();
        if (fallback is null)
            return ValueTask.FromResult(new EvalAssertionResult(EvalAssertionStatus.Pass, Score: 1.0, Reason: null));

        return ValueTask.FromResult(new EvalAssertionResult(
            EvalAssertionStatus.Fail,
            Score: 0.0,
            Reason: $"LLM fallback engaged: provider[{fallback.FromProviderIndex}] → provider[{fallback.ToProviderIndex}] (reason: {fallback.Reason})"));
    }

    private bool MatchesConcept(RunHealthSignalKind kind)
    {
        if (_conceptFilter is null) return true;
        if (_catalog is null) return true;
        var concept = _catalog.FromSignalKind(kind);
        return concept is not null && _catalog.IsMatchOrDescendant(concept.Name, _conceptFilter);
    }
}

/// <summary>Factory for <see cref="NoFallbackEngagedAssertion"/>.</summary>
internal sealed class NoFallbackEngagedAssertionFactory : IEvalAssertionFactory
{
    /// <inheritdoc/>
    public string Kind => "no-fallback-engaged";

    /// <inheritdoc/>
    public IEvalAssertion Create(JsonElement args, IServiceProvider services)
    {
        string? conceptFilter = null;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("concept", out var cp))
            conceptFilter = cp.GetString();
        var catalog = services.GetService<IFailureOntologyCatalog>();
        return new NoFallbackEngagedAssertion(conceptFilter, catalog);
    }
}
