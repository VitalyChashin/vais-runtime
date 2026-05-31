// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Observability.RunHealthStore;

/// <summary>
/// Durable store for per-run mechanical-failure signals — the persistence the Part 1
/// severity signals (<see cref="ToolCallCompleted"/>.<c>Level</c>, <see cref="LlmCallRetried"/>,
/// <see cref="LlmFallbackEngaged"/>, <see cref="TurnFailed"/>, degraded <see cref="TurnCompleted"/>,
/// <see cref="GuardrailTriggered"/>) lacked. Those are published to <see cref="IAgentEventBus"/>
/// but no prior subscriber persisted them, so a recovered failure vanished once the turn ended.
/// <see cref="RunHealthSignalSubscriber"/> writes here; the run-health aggregator reads here to
/// roll a whole run tree up into a <see cref="RunHealth"/>.
/// </summary>
public interface IRunHealthStore
{
    /// <summary>Idempotently creates the <c>vais_run_health_signals</c> schema. Called once on startup.</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Persists one mechanical-failure signal. Idempotent under at-least-once / per-silo
    /// duplicate delivery (ADR 019): the composite key <c>(run_id, at, signal_kind, source)</c>
    /// collapses re-deliveries of the same event via <c>ON CONFLICT DO NOTHING</c>.
    /// </summary>
    Task RecordSignalAsync(RunHealthSignalRecord record, CancellationToken ct = default);

    /// <summary>
    /// Returns every signal for a run <em>tree</em> rooted at <paramref name="rootRunId"/> — the
    /// exact run plus all agent-as-tool descendants, whose run ids follow the
    /// <c>{parentRun}__{name}__{hash}</c> convention. Ordered by <c>at</c> ascending.
    /// </summary>
    Task<IReadOnlyList<RunHealthSignal>> ListByRunTreeAsync(string rootRunId, CancellationToken ct = default);

    /// <summary>Deletes signals whose <c>created_at</c> is older than <paramref name="cutoff"/>.</summary>
    Task DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}

/// <summary>
/// Write shape for <see cref="IRunHealthStore.RecordSignalAsync"/>: a <see cref="RunHealthSignal"/>
/// plus the run/correlation keys needed to roll it up. The read side returns the projected
/// <see cref="RunHealthSignal"/> (run/correlation are query parameters, not result fields).
/// </summary>
/// <param name="RunId">The run that produced the signal (may be a child run in the tree).</param>
/// <param name="CorrelationId">Cross-service correlation id, when set; shared across a run tree.</param>
/// <param name="Source">Attribution: sub-agent name, graph node id, or tool name. Empty string when unknown.</param>
/// <param name="Kind">The kind of mechanical failure.</param>
/// <param name="Level">Severity: Warning (recovered) or Error (fatal).</param>
/// <param name="ErrorType">CLR/error type name when known.</param>
/// <param name="IsTransient">Whether the underlying failure was classified transient.</param>
/// <param name="At">UTC timestamp of the signal — part of the dedup key.</param>
/// <param name="ConceptName">Ontology concept name stamped by <c>RunHealthSignalSubscriber</c>. Null for pre-2a rows.</param>
/// <param name="AttributionPath">Deployment attribution from Part 2b. Null in Part 2a.</param>
public sealed record RunHealthSignalRecord(
    string RunId,
    string? CorrelationId,
    string Source,
    RunHealthSignalKind Kind,
    FailureLevel Level,
    string? ErrorType,
    bool IsTransient,
    DateTimeOffset At,
    string? ConceptName = null,
    string? AttributionPath = null);
