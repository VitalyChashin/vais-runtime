// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents;

/// <summary>
/// Persistence contract for graph-run state. One checkpoint per super-step boundary;
/// implementations ship in-memory (dev/tests) and Orleans-grain-backed (durable).
/// Parallel shape to <c>A2A.ITaskStore</c> from the v0.8 A2A inbound pillar.
/// </summary>
/// <remarks>
/// <para>
/// <b>Thread-scoped by <see cref="GraphCheckpoint.RunId"/>.</b> Same semantics as
/// LangGraph's <c>thread_id</c> — each run has an independent checkpoint timeline.
/// Consumers with concurrent runs use distinct <c>RunId</c>s.
/// </para>
/// <para>
/// <b>Minimal 3-verb surface.</b> v0.9 doesn't ship LangGraph's <c>put_writes</c>
/// (partial super-step writes) because our orchestrator doesn't parallelise nodes
/// within a super-step. A future pillar adds it if fan-out lands.
/// </para>
/// </remarks>
public interface IGraphCheckpointer
{
    /// <summary>Upsert the checkpoint for <paramref name="checkpoint"/>'s run.</summary>
    ValueTask SaveAsync(GraphCheckpoint checkpoint, CancellationToken cancellationToken = default);

    /// <summary>Load the latest checkpoint for <paramref name="runId"/>, or <c>null</c> if none exists.</summary>
    ValueTask<GraphCheckpoint?> LoadAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>Delete all checkpoints for <paramref name="runId"/>. Idempotent.</summary>
    ValueTask DeleteAsync(string runId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Persisted snapshot of graph-run state at a super-step boundary. Serialisation
/// contract: everything in this record round-trips through System.Text.Json using
/// <see cref="JsonSerializerOptions.Default"/> — checkpoint implementations can
/// persist as JSON text (<c>InMemoryCheckpointer</c> / future Orleans grain)
/// without custom converters.
/// </summary>
/// <param name="RunId">Run correlation id. Matches <see cref="AgentGraphEvent.RunId"/>.</param>
/// <param name="GraphId">Id of the <see cref="AgentGraphManifest"/> this checkpoint was produced against.</param>
/// <param name="GraphVersion">Version of the manifest. Resume compatibility check uses this.</param>
/// <param name="State">Graph state at the checkpoint boundary. Keys match the manifest's state schema (if declared).</param>
/// <param name="NextNodeId">Node that should execute when the run resumes. Null when <see cref="IsComplete"/> is true.</param>
/// <param name="SuperStepIndex">Zero-based super-step count since run start.</param>
/// <param name="PendingInterruptId">Correlation id from a <see cref="GraphInterrupted"/> event. Set when the graph is paused awaiting resume; null otherwise.</param>
/// <param name="IsComplete">True when the graph reached an <c>End</c> node. Terminal checkpoints are retained for audit until explicit <see cref="IGraphCheckpointer.DeleteAsync"/>.</param>
/// <param name="CreatedAt">UTC timestamp of save.</param>
public sealed record GraphCheckpoint(
    string RunId,
    string GraphId,
    string GraphVersion,
    IReadOnlyDictionary<string, JsonElement> State,
    string? NextNodeId,
    int SuperStepIndex,
    string? PendingInterruptId,
    bool IsComplete,
    DateTimeOffset CreatedAt);

/// <summary>
/// Thrown when a graph exceeds its <see cref="AgentGraphManifest.MaxSteps"/> ceiling
/// (or the runtime default of 1000). Prevents runaway cycles. Caller can increase the
/// ceiling on the manifest or introduce a termination predicate on the cycling edge.
/// </summary>
public sealed class GraphRecursionException : Exception
{
    /// <summary>Graph id where the ceiling was hit.</summary>
    public string GraphId { get; }

    /// <summary>Configured max-step ceiling that was exceeded.</summary>
    public int MaxSteps { get; }

    /// <summary>Construct the exception for a specific graph + ceiling.</summary>
    public GraphRecursionException(string graphId, int maxSteps)
        : base($"Graph '{graphId}' exceeded its maxSteps ceiling of {maxSteps}. Check for runaway cycles or raise MaxSteps.")
    {
        GraphId = graphId;
        MaxSteps = maxSteps;
    }
}
