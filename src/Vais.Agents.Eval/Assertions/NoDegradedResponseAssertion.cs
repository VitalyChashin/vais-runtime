// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Vais.Agents.Eval.Assertions;

/// <summary>
/// Passes when no turn completed with a degraded/partial result during the case run.
/// Catches the L3 plugin-fallback pattern: a turn that calls <c>is_partial=true</c> or returns a placeholder
/// ("No analysis produced.") emits a <see cref="TurnCompleted"/> with <c>Level=Warning</c> rather than a
/// <see cref="TurnFailed"/> — so <see cref="NoTurnFailedAssertion"/> would pass while the output was silently degraded.
/// Wire as <c>{ "kind": "no-degraded-response" }</c> (no filter) or
/// <c>{ "kind": "no-degraded-response", "concept": "PluginPartial" }</c> to scope to a concept subtree.
/// </summary>
internal sealed class NoDegradedResponseAssertion : IEvalAssertion
{
    private readonly string? _conceptFilter;
    private readonly IFailureOntologyCatalog? _catalog;

    internal NoDegradedResponseAssertion(string? conceptFilter = null, IFailureOntologyCatalog? catalog = null)
    {
        _conceptFilter = conceptFilter;
        _catalog = catalog;
    }

    /// <inheritdoc/>
    public string Kind => "no-degraded-response";

    /// <inheritdoc/>
    public ValueTask<EvalAssertionResult> EvaluateAsync(EvalCaseContext ctx, EvalRunRecord run, CancellationToken ct)
    {
        if (!MatchesConcept(RunHealthSignalKind.TurnPartial))
            return ValueTask.FromResult(new EvalAssertionResult(EvalAssertionStatus.Pass, Score: 1.0, Reason: null));

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

    private bool MatchesConcept(RunHealthSignalKind kind)
    {
        if (_conceptFilter is null) return true;
        if (_catalog is null) return true;
        var concept = _catalog.FromSignalKind(kind);
        return concept is not null && _catalog.IsMatchOrDescendant(concept.Name, _conceptFilter);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>Factory for <see cref="NoDegradedResponseAssertion"/>.</summary>
internal sealed class NoDegradedResponseAssertionFactory : IEvalAssertionFactory
{
    /// <inheritdoc/>
    public string Kind => "no-degraded-response";

    /// <inheritdoc/>
    public IEvalAssertion Create(JsonElement args, IServiceProvider services)
    {
        string? conceptFilter = null;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("concept", out var cp))
            conceptFilter = cp.GetString();
        var catalog = services.GetService<IFailureOntologyCatalog>();
        return new NoDegradedResponseAssertion(conceptFilter, catalog);
    }
}
