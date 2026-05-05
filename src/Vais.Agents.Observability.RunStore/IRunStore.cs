// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Observability.RunStore;

/// <summary>
/// Storage back-end for per-run and per-node execution history.
/// Receives write calls from <c>RunStoreSubscriber</c> (which subscribes to
/// <see cref="Vais.Agents.IAgentGraphEventBus"/>) and exposes read queries for CLI and Workbench.
/// </summary>
public interface IRunStore
{
    /// <summary>Idempotently creates the required schema. Called once on startup.</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Records that a graph run has begun.</summary>
    Task StartRunAsync(string runId, string graphId, CancellationToken ct = default);

    /// <summary>Marks a run as completed and records its final super-step count.</summary>
    Task CompleteRunAsync(string runId, int superSteps, CancellationToken ct = default);

    /// <summary>Marks a run as failed and stores the error message.</summary>
    Task FailRunAsync(string runId, string error, CancellationToken ct = default);

    /// <summary>Marks a run as interrupted, storing the interrupt ID as the error field.</summary>
    Task InterruptRunAsync(string runId, string interruptId, CancellationToken ct = default);

    /// <summary>Records that a node has started executing.</summary>
    Task StartNodeAsync(string runId, string nodeId, string nodeKind, string? agentId, CancellationToken ct = default);

    /// <summary>Marks a node as completed and computes its duration.</summary>
    Task CompleteNodeAsync(string runId, string nodeId, CancellationToken ct = default);

    /// <summary>Stores the truncated input/output text and token counts from an agent invocation.</summary>
    Task RecordNodeInvocationAsync(string runId, string nodeId, string agentId,
        string inputText, string outputText, int inputTokens, int outputTokens,
        CancellationToken ct = default);

    /// <summary>Appends an outgoing edge target to the source node's <c>edges_taken</c> list.</summary>
    Task RecordEdgeAsync(string runId, string fromNodeId, string toNodeId, CancellationToken ct = default);

    /// <summary>Deletes runs (and their nodes) whose <c>created_at</c> is older than <paramref name="cutoff"/>.</summary>
    Task DeleteRunsOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default);

    /// <summary>Lists runs for a graph, optionally filtered by status and time window.</summary>
    Task<IReadOnlyList<PipelineRun>> ListRunsAsync(string graphId, RunStatus? status = null,
        DateTimeOffset? since = null, DateTimeOffset? until = null, int limit = 20,
        CancellationToken ct = default);

    /// <summary>Returns a single run by its ID, or <see langword="null"/> if not found.</summary>
    Task<PipelineRun?> GetRunAsync(string runId, CancellationToken ct = default);

    /// <summary>Returns all node executions for a run, ordered by start time.</summary>
    Task<IReadOnlyList<NodeExecution>> GetNodesAsync(string runId, CancellationToken ct = default);

    /// <summary>Returns a single node execution, or <see langword="null"/> if not found.</summary>
    Task<NodeExecution?> GetNodeAsync(string runId, string nodeId, CancellationToken ct = default);

    /// <summary>Returns node executions for a specific agent across all graph runs, ordered by start time descending.</summary>
    Task<IReadOnlyList<NodeExecution>> ListNodeExecutionsByAgentAsync(string agentId,
        DateTimeOffset? since = null, DateTimeOffset? until = null, int limit = 20,
        CancellationToken ct = default);
}
