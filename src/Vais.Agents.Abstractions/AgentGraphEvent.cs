// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Base type for events emitted by an <see cref="IAgentGraph{TState}"/> during graph
/// execution. Parallel to <see cref="AgentEvent"/> (per-turn taxonomy) but graph-scoped —
/// carries <see cref="RunId"/> + super-step index so consumers can correlate against
/// the checkpoint timeline.
/// </summary>
/// <remarks>
/// Closed hierarchy. Consumers pattern-match on subtype; adding a new subtype is
/// an <em>unshipped</em> addition to Abstractions.
/// </remarks>
/// <param name="At">UTC timestamp when the event was emitted.</param>
/// <param name="Context">Ambient agent context at event-emission time.</param>
/// <param name="RunId">Graph-run correlation id — matches the checkpointer's run key.</param>
/// <param name="SuperStep">Zero-based super-step index at emission time.</param>
public abstract record AgentGraphEvent(
    DateTimeOffset At,
    AgentContext Context,
    string RunId,
    int SuperStep);

/// <summary>Emitted once, at the very start of a graph run, before the entry node executes.</summary>
public sealed record GraphStarted(
    DateTimeOffset At,
    AgentContext Context,
    string RunId,
    int SuperStep,
    string GraphId,
    string GraphVersion,
    string EntryNodeId)
    : AgentGraphEvent(At, Context, RunId, SuperStep);

/// <summary>Emitted before a node executes.</summary>
public sealed record NodeStarted(
    DateTimeOffset At,
    AgentContext Context,
    string RunId,
    int SuperStep,
    string NodeId,
    string NodeKind)
    : AgentGraphEvent(At, Context, RunId, SuperStep);

/// <summary>Emitted after a node executes successfully, before outgoing edges are evaluated.</summary>
public sealed record NodeCompleted(
    DateTimeOffset At,
    AgentContext Context,
    string RunId,
    int SuperStep,
    string NodeId,
    string NodeKind,
    TimeSpan Duration)
    : AgentGraphEvent(At, Context, RunId, SuperStep);

/// <summary>
/// Emitted once per edge traversal, after the edge's predicate matches and its
/// <see cref="GraphEdge.OnTraverse"/> effect (if any) has been applied.
/// </summary>
public sealed record EdgeTraversed(
    DateTimeOffset At,
    AgentContext Context,
    string RunId,
    int SuperStep,
    string From,
    string To)
    : AgentGraphEvent(At, Context, RunId, SuperStep);

/// <summary>
/// Emitted after a state-mutating effect applied by an edge or node binding.
/// Keys list the state properties that changed in this mutation.
/// </summary>
public sealed record StateUpdated(
    DateTimeOffset At,
    AgentContext Context,
    string RunId,
    int SuperStep,
    IReadOnlyList<string> ChangedKeys)
    : AgentGraphEvent(At, Context, RunId, SuperStep);

/// <summary>
/// Emitted when the graph hits an <c>Interrupt</c>-kind node. The graph pauses;
/// a checkpoint has been written under <see cref="InterruptId"/> for the caller
/// to resume with <see cref="IAgentGraph{T}.InvokeAsync"/>.
/// </summary>
public sealed record GraphInterrupted(
    DateTimeOffset At,
    AgentContext Context,
    string RunId,
    int SuperStep,
    string NodeId,
    string InterruptId,
    string? Reason)
    : AgentGraphEvent(At, Context, RunId, SuperStep);

/// <summary>Emitted when a previously-interrupted graph resumes from a checkpoint.</summary>
public sealed record GraphResumed(
    DateTimeOffset At,
    AgentContext Context,
    string RunId,
    int SuperStep,
    string ResumedFromNodeId,
    string InterruptId)
    : AgentGraphEvent(At, Context, RunId, SuperStep);

/// <summary>Emitted when the graph reaches an <c>End</c> node or completes naturally.</summary>
public sealed record GraphCompleted(
    DateTimeOffset At,
    AgentContext Context,
    string RunId,
    int SuperStep,
    string FinalNodeId,
    TimeSpan Duration)
    : AgentGraphEvent(At, Context, RunId, SuperStep);

/// <summary>Emitted when the graph fails — unhandled exception, max-steps hit, manifest error.</summary>
public sealed record GraphFailed(
    DateTimeOffset At,
    AgentContext Context,
    string RunId,
    int SuperStep,
    string ErrorType,
    string ErrorMessage,
    TimeSpan Duration)
    : AgentGraphEvent(At, Context, RunId, SuperStep);
