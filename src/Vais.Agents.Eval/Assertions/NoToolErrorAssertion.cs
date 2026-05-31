// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Vais.Agents.Eval.Assertions;

/// <summary>
/// Passes when no tool invocation failed during the case run.
/// Catches recovered tool errors that <see cref="NoTurnFailedAssertion"/> misses — a tool error
/// that the model reads and recovers from does not produce a <see cref="TurnFailed"/> event, but
/// does produce a <see cref="ToolCallCompleted"/> with <c>Succeeded=false</c>.
/// Wire as <c>{ "kind": "no-tool-error" }</c> (no filter) or
/// <c>{ "kind": "no-tool-error", "concept": "McpToolError" }</c> to scope to a concept subtree.
/// </summary>
internal sealed class NoToolErrorAssertion : IEvalAssertion
{
    private readonly string? _conceptFilter;
    private readonly IFailureOntologyCatalog? _catalog;

    internal NoToolErrorAssertion(string? conceptFilter = null, IFailureOntologyCatalog? catalog = null)
    {
        _conceptFilter = conceptFilter;
        _catalog = catalog;
    }

    /// <inheritdoc/>
    public string Kind => "no-tool-error";

    /// <inheritdoc/>
    public ValueTask<EvalAssertionResult> EvaluateAsync(EvalCaseContext ctx, EvalRunRecord run, CancellationToken ct)
    {
        var failed = run.Events.OfType<ToolCallCompleted>()
            .FirstOrDefault(e => !e.Succeeded && MatchesConcept(RunHealthSignalKind.ToolError));

        if (failed is null)
            return ValueTask.FromResult(new EvalAssertionResult(EvalAssertionStatus.Pass, Score: 1.0, Reason: null));

        return ValueTask.FromResult(new EvalAssertionResult(
            EvalAssertionStatus.Fail,
            Score: 0.0,
            Reason: $"Tool '{failed.ToolName}' failed: {failed.Error ?? "unknown error"}"));
    }

    private bool MatchesConcept(RunHealthSignalKind kind)
    {
        if (_conceptFilter is null) return true;
        if (_catalog is null) return true;
        var concept = _catalog.FromSignalKind(kind);
        return concept is not null && _catalog.IsMatchOrDescendant(concept.Name, _conceptFilter);
    }
}

/// <summary>Factory for <see cref="NoToolErrorAssertion"/>.</summary>
internal sealed class NoToolErrorAssertionFactory : IEvalAssertionFactory
{
    /// <inheritdoc/>
    public string Kind => "no-tool-error";

    /// <inheritdoc/>
    public IEvalAssertion Create(JsonElement args, IServiceProvider services)
    {
        string? conceptFilter = null;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("concept", out var cp))
            conceptFilter = cp.GetString();
        var catalog = services.GetService<IFailureOntologyCatalog>();
        return new NoToolErrorAssertion(conceptFilter, catalog);
    }
}
