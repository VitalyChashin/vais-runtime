// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vais.Agents;

/// <summary>
/// Side-effect applied to graph state when an edge is traversed. Closed hierarchy
/// shipping three primitive verbs (<see cref="Set"/>, <see cref="Increment"/>,
/// <see cref="Append"/>) plus a <see cref="HandlerRef"/> escape hatch for richer
/// mutations via a DI-resolved <see cref="IGraphEdgeEffect"/>.
/// </summary>
[JsonConverter(typeof(GraphEdgeEffectJsonConverter))]
public abstract record GraphEdgeEffect
{
    private GraphEdgeEffect() { }

    /// <summary>Sets <see cref="Property"/> in state to <see cref="Value"/>.</summary>
    public sealed record Set(string Property, JsonElement Value) : GraphEdgeEffect;

    /// <summary>
    /// Increments numeric <see cref="Property"/> by <see cref="By"/> (default <c>1</c>).
    /// Initialises to <see cref="By"/> if the property doesn't exist.
    /// </summary>
    public sealed record Increment(string Property, int By = 1) : GraphEdgeEffect;

    /// <summary>
    /// Appends <see cref="Value"/> to array <see cref="Property"/>. Initialises to
    /// a single-element array if the property doesn't exist.
    /// </summary>
    public sealed record Append(string Property, JsonElement Value) : GraphEdgeEffect;

    /// <summary>Dispatches to a DI-resolved <see cref="IGraphEdgeEffect"/> implementation.</summary>
    public sealed record HandlerRef(GraphHandlerRef Handler) : GraphEdgeEffect;
}

/// <summary>
/// Consumer hook for <see cref="GraphEdgeEffect.HandlerRef"/>. Resolved from DI at
/// edge-traversal time; must be registered before graph invocation.
/// </summary>
public interface IGraphEdgeEffect
{
    /// <summary>Mutate graph state in place. Called after the edge predicate passes and before the target node is invoked.</summary>
    /// <param name="state">Mutable graph state. Implementations mutate this dictionary directly.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask ApplyAsync(
        IDictionary<string, JsonElement> state,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Consumer hook for <c>Code</c>-kind <see cref="GraphNode"/>s. Resolved from DI at
/// node-entry time; receives a read-only view of the graph-state subset described
/// by <see cref="GraphStateBindings.Input"/> and returns a dictionary of output
/// updates filtered to <see cref="GraphStateBindings.Output"/> by the orchestrator.
/// </summary>
public interface IGraphCodeNode
{
    /// <summary>Execute the node.</summary>
    /// <param name="input">Bound input subset of graph state.</param>
    /// <param name="context">Ambient agent context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<IReadOnlyDictionary<string, JsonElement>> ExecuteAsync(
        IReadOnlyDictionary<string, JsonElement> input,
        AgentContext context,
        CancellationToken cancellationToken = default);
}
