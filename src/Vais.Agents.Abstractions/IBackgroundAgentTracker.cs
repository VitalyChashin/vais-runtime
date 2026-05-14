// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Tracks background (fire-and-forget) agent sub-runs started by a coordinator
/// via a <c>mode: background</c> <c>localAgents[]</c> entry. Implementations:
/// <list type="bullet">
///   <item><description><c>InMemoryBackgroundAgentTracker</c> — dev/test only (P1 scaling gap: process-local, lost on restart).</description></item>
///   <item><description><c>OrleansBackgroundAgentTracker</c> — production (durable grain-backed, survives silo restart, cluster-wide visibility).</description></item>
/// </list>
/// </summary>
public interface IBackgroundAgentTracker
{
    /// <summary>
    /// Enqueues a background run and returns its durable handle (the child session id).
    /// The run executes asynchronously; poll via <see cref="GetAsync"/> to observe completion.
    /// </summary>
    ValueTask<string> StartAsync(
        string parentRunId,
        string childAgentId,
        string childSessionId,
        string message,
        AgentContext childContext,
        CancellationToken ct = default);

    /// <summary>Returns the run record for <paramref name="handle"/>, or <c>null</c> if not found.</summary>
    ValueTask<BackgroundAgentRunRecord?> GetAsync(string handle, CancellationToken ct = default);

    /// <summary>Lists all background runs that share <paramref name="parentRunId"/>.</summary>
    ValueTask<IReadOnlyList<BackgroundAgentRunRecord>> ListAsync(string parentRunId, CancellationToken ct = default);

    /// <summary>
    /// Requests cancellation of the run identified by <paramref name="handle"/>.
    /// Returns <c>true</c> if the run was found and a cancellation request was accepted.
    /// </summary>
    ValueTask<bool> CancelAsync(string handle, CancellationToken ct = default);
}

/// <summary>
/// Snapshot of a single background agent sub-run.
/// </summary>
/// <param name="Handle">Durable handle — equals the child session id.</param>
/// <param name="ParentRunId">Run id of the coordinator that started this sub-run.</param>
/// <param name="ChildAgentId">Agent id of the sub-agent.</param>
/// <param name="Status">Current status.</param>
/// <param name="StartedAt">When the run was enqueued.</param>
/// <param name="CompletedAt">When the run reached a terminal status, or <c>null</c> if still active.</param>
/// <param name="Result">Final text returned by the sub-agent on success, or <c>null</c>.</param>
/// <param name="Error">Error message on failure, or <c>null</c>.</param>
public sealed record BackgroundAgentRunRecord(
    string Handle,
    string ParentRunId,
    string ChildAgentId,
    BackgroundAgentRunStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt = null,
    string? Result = null,
    string? Error = null);

/// <summary>Lifecycle states for a background agent sub-run.</summary>
public enum BackgroundAgentRunStatus
{
    /// <summary>Enqueued; the run has not started yet.</summary>
    Pending = 0,

    /// <summary>The sub-agent is executing.</summary>
    Running = 1,

    /// <summary>The sub-agent returned a result successfully.</summary>
    Completed = 2,

    /// <summary>The sub-agent threw an unhandled exception.</summary>
    Failed = 3,

    /// <summary>Cancelled via <see cref="IBackgroundAgentTracker.CancelAsync"/>.</summary>
    Cancelled = 4,
}
