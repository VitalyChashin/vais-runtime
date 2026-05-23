// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents;

/// <summary>
/// Base class for graph-node middleware. Override <see cref="InvokeAsync"/> to wrap a graph node's
/// body execution. Call <c>next</c> to run the node body and observe / transform its output; do NOT
/// call <c>next</c> to short-circuit (return a substitute output without running the node).
/// </summary>
/// <remarks>
/// <para>
/// Fires once per node execution, around the node body, in the runtime's graph orchestrator. It wraps
/// <c>Agent</c> and <c>Code</c> nodes (the kinds with an executable body); control nodes
/// (<c>End</c> / <c>Interrupt</c> / <c>Fork</c>) are not wrapped. Multiple middleware run in ascending
/// priority; the chain nests so the highest-priority handler is outermost.
/// </para>
/// <para>
/// <b>Short-circuit is journaling-safe.</b> The wrap sits around the node body only — the resulting
/// output (whether from the body or a short-circuit substitute) flows through the orchestrator's
/// normal state-merge and per-step checkpoint. A short-circuited node is therefore recorded as
/// completed with its output exactly as a real run would be, so resume stays consistent and P2
/// (one-node-one-turn) is preserved.
/// </para>
/// <para>Instances must be reentrant — no per-call state in fields; use locals inside <see cref="InvokeAsync"/>.</para>
/// </remarks>
public abstract class GraphNodeMiddleware
{
    /// <summary>
    /// Wraps a graph node's body. The default implementation is a pass-through.
    /// </summary>
    public virtual Task<GraphNodeOutcome> InvokeAsync(
        GraphNodeContext context,
        Func<Task<GraphNodeOutcome>> next,
        CancellationToken cancellationToken = default)
        => next();
}

/// <summary>
/// The node about to execute, passed to a <see cref="GraphNodeMiddleware"/>.
/// </summary>
/// <param name="RunId">The graph run id.</param>
/// <param name="NodeId">The graph node id.</param>
/// <param name="NodeKind">The node kind — <c>"Agent"</c> or <c>"Code"</c>.</param>
/// <param name="AgentId">The node's agent ref id; empty for non-agent (<c>Code</c>) nodes.</param>
/// <param name="SuperStep">The orchestrator super-step index this node executes in.</param>
/// <param name="Input">The node's binding-filtered input state.</param>
public sealed record GraphNodeContext(
    string RunId,
    string NodeId,
    string NodeKind,
    string AgentId,
    int SuperStep,
    IReadOnlyDictionary<string, JsonElement> Input);

/// <summary>
/// The result of a node body — the state contribution the node produces. Returned from
/// <c>next</c> (the real body) or constructed by a handler to substitute / transform the output.
/// </summary>
/// <param name="Output">The node's output state, merged into the graph state by the orchestrator.</param>
public sealed record GraphNodeOutcome(
    IReadOnlyDictionary<string, JsonElement> Output);
