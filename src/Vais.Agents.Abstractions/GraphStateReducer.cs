// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents;

/// <summary>
/// Declarative reducer specification for a single graph-state key. Declared in
/// <see cref="AgentGraphManifest.StateReducers"/> to override the default last-write-wins
/// strategy on a per-key basis. Parallel closed hierarchy to <see cref="GraphEdgePredicate"/>
/// and <see cref="GraphEdgeEffect"/> — three built-in cases plus a <see cref="HandlerRef"/>
/// escape hatch for arbitrary C# logic via DI.
/// </summary>
/// <remarks>
/// Precedence rule: if the manifest declares a reducer for a key, it always wins over the
/// built-in implicit defaults (last-write-wins for general keys, append for
/// <c>messages</c>). Declare <see cref="LastWriteWins"/> explicitly to opt the
/// <c>messages</c> key out of its default append behaviour.
/// </remarks>
public abstract record GraphStateReducer
{
    private GraphStateReducer() { }

    /// <summary>
    /// Last-write-wins — always replaces the existing value with the incoming one.
    /// The runtime default for all keys not explicitly declared in
    /// <see cref="AgentGraphManifest.StateReducers"/>, except the well-known
    /// <c>messages</c> key (which defaults to <see cref="Append"/>).
    /// </summary>
    public sealed record LastWriteWins : GraphStateReducer;

    /// <summary>
    /// Array-append reducer. If both sides are JSON arrays, concatenates them;
    /// if either side is non-array, falls through to last-write-wins. Identical
    /// semantics to the implicit <c>messages</c> default — generalised to any
    /// user-named key.
    /// </summary>
    public sealed record Append : GraphStateReducer;

    /// <summary>
    /// Dispatches to a DI-resolved <see cref="IGraphStateReducer"/> implementation.
    /// Resolved by the orchestrator at merge time using the caller-supplied
    /// <c>reducerResolver</c> delegate.
    /// </summary>
    public sealed record HandlerRef(GraphHandlerRef Handler) : GraphStateReducer;
}

/// <summary>
/// Consumer hook for <see cref="GraphStateReducer.HandlerRef"/>. Resolved from DI
/// at merge time; must be registered before graph invocation for the referenced
/// <see cref="GraphHandlerRef.TypeName"/>.
/// </summary>
public interface IGraphStateReducer
{
    /// <summary>
    /// Produce the merged state value for a single key.
    /// Called once per key whose manifest reducer points to this handler.
    /// </summary>
    /// <param name="existing">Current value in graph state. May be default if the key does not yet exist.</param>
    /// <param name="incoming">Value produced by the node output.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<JsonElement> ReduceAsync(
        JsonElement existing,
        JsonElement incoming,
        CancellationToken cancellationToken = default);
}
