// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>Terminal outcome reported to <see cref="IGraphRunCoordinator.CompleteAsync"/>.</summary>
public enum GraphRunOutcome
{
    /// <summary>Run reached an End node.</summary>
    Completed = 0,
    /// <summary>Run paused at an Interrupt node.</summary>
    Interrupted = 1,
    /// <summary>Run threw / hit max-steps / manifest error.</summary>
    Failed = 2,
    /// <summary>Run was cancelled.</summary>
    Cancelled = 3,
}

/// <summary>Point-in-time view of a tracked graph run.</summary>
/// <param name="RunId">Run correlation id.</param>
/// <param name="GraphId">Graph manifest id the run belongs to.</param>
/// <param name="Version">Graph manifest version.</param>
/// <param name="StartedAt">UTC start time.</param>
/// <param name="CancelRequested">Whether cancellation has been requested.</param>
public sealed record GraphRunSnapshot(
    string RunId,
    string GraphId,
    string Version,
    DateTimeOffset StartedAt,
    bool CancelRequested);

/// <summary>
/// Tracks in-flight graph runs — conflict detection, cancellation signalling, and status —
/// behind an abstraction so the run registry can be in-process (single host) or grain-backed
/// (cluster-wide). The in-process default lives in <c>Vais.Agents.Control.InProcess</c>; the
/// Orleans implementation lives in <c>Vais.Agents.Hosting.Orleans</c> and makes cancel/status
/// reachable from any silo (P1).
/// </summary>
/// <remarks>
/// Cancellation is cooperative and decoupled: <see cref="RequestCancelAsync"/> records a durable
/// flag; the silo executing the run observes it (directly for same-host runs, or via
/// <see cref="IsCancelRequestedAsync"/> polling for cross-silo runs) and stops at the next
/// super-step boundary. The coordinator never holds a <see cref="System.Threading.CancellationTokenSource"/>
/// — those are silo-local and meaningless across a cluster.
/// </remarks>
public interface IGraphRunCoordinator
{
    /// <summary>
    /// Register a fresh run. Returns <see langword="false"/> if a run with <paramref name="runId"/>
    /// is already active (caller should throw <see cref="GraphRunConflictException"/>).
    /// </summary>
    ValueTask<bool> TryStartAsync(string runId, string graphId, string version, CancellationToken ct = default);

    /// <summary>
    /// Re-register a run that is resuming from an interrupt. Unconditional — resume reuses an
    /// existing run id and must not be rejected as a conflict.
    /// </summary>
    ValueTask MarkActiveAsync(string runId, string graphId, string version, CancellationToken ct = default);

    /// <summary>Request cancellation of a run. Durable and reachable from any host.</summary>
    ValueTask RequestCancelAsync(string runId, CancellationToken ct = default);

    /// <summary>Whether cancellation has been requested for a run (false if the run is unknown).</summary>
    ValueTask<bool> IsCancelRequestedAsync(string runId, CancellationToken ct = default);

    /// <summary>Mark a run terminal and release its tracking slot.</summary>
    ValueTask CompleteAsync(string runId, GraphRunOutcome outcome, CancellationToken ct = default);

    /// <summary>Snapshot of a tracked run, or <see langword="null"/> if it is not active.</summary>
    ValueTask<GraphRunSnapshot?> GetAsync(string runId, CancellationToken ct = default);
}
