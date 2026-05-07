// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents;

/// <summary>
/// Executes an <see cref="AgentGraphManifest"/> — drives the BSP super-step loop,
/// evaluates edge predicates, applies edge effects, invokes nodes, merges their
/// outputs back into state, and checkpoints at each super-step boundary.
/// Stack-neutral; the Pregel/BSP shape is shared with MAF Workflows and LangGraph.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hybrid state model.</b> Use <see cref="IAgentGraph{TState}"/> for typed
/// code-first graphs (any POCO round-tripped through System.Text.Json) and
/// <see cref="IAgentGraph"/> for declarative graphs authored in YAML/JSON
/// (state is a <see cref="IDictionary{TKey,TValue}"/> of <see cref="JsonElement"/>
/// values with a JSON Schema constraining the shape). Both share the same runtime.
/// </para>
/// <para>
/// <b>Sibling of <see cref="IAgentOrchestrator"/>, not a subtype.</b> Graph runs
/// are state-threaded, multi-step, and checkpointable — forcing inheritance
/// would compromise both contracts. Consumers needing a v0.4-style speaker-list
/// orchestration continue to use <see cref="IAgentOrchestrator"/>.
/// </para>
/// </remarks>
/// <typeparam name="TState">State object threaded through the graph. Must round-trip through System.Text.Json.</typeparam>
public interface IAgentGraph<TState>
{
    /// <summary>
    /// Run the graph from the entry node to an <c>End</c> node (or interrupt) with
    /// <paramref name="initial"/> as the starting state. Returns the final state.
    /// </summary>
    ValueTask<TState> InvokeAsync(
        TState initial,
        AgentContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Run the graph and stream the full <see cref="AgentGraphEvent"/> taxonomy —
    /// start / node-start / node-complete / edge-traversal / state-update / interrupt
    /// / resume / complete / fail.
    /// </summary>
    /// <remarks>
    /// <b>Event ordering contract.</b> For each node execution, <see cref="NodeCompleted"/>
    /// is emitted before any <see cref="StateUpdated"/> events produced by that node's
    /// output bindings. All <see cref="IAgentGraph{TState}"/> implementations must honor
    /// this ordering so consumers can correlate state changes to the node that caused them.
    /// </remarks>
    IAsyncEnumerable<AgentGraphEvent> StreamAsync(
        TState initial,
        AgentContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Non-generic specialisation over a shared-bag state type
/// (<see cref="IDictionary{TKey,TValue}"/> with <see cref="JsonElement"/> values).
/// Used by declarative YAML-authored graphs where the state shape is constrained
/// only by the manifest's JSON Schema block.
/// </summary>
public interface IAgentGraph : IAgentGraph<IDictionary<string, JsonElement>>
{
}
