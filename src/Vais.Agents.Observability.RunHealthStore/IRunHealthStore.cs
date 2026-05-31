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

    /// <summary>
    /// Part 2c (DM-4) — cross-run signal search. Returns the most recent bus-sourced signals
    /// matching the optional filters. Concept matching is exact on <c>concept_name</c>;
    /// the caller is expected to expand the catalog parent walk (via <c>IFailureOntologyCatalog.IsMatchOrDescendant</c>)
    /// and pass the resolved concept set, or filter post-fetch.
    /// MCP / LLM gateway / NodeFailed / background failures are NOT in this store — they live in
    /// <c>IMcpGatewayEventStore</c> / <c>IGatewayEventStore</c> / the run + background stores and
    /// are synthesised per-run by the aggregator. Cross-run queries for those concepts must hit
    /// their respective stores.
    /// </summary>
    /// <param name="conceptName">Filter to a specific concept name (exact match). Null returns all.</param>
    /// <param name="agentName">Filter to signals whose Source equals this agent or whose CorrelationId matches. Null returns all.</param>
    /// <param name="since">Earliest <c>at</c> timestamp to include. Null means no lower bound.</param>
    /// <param name="limit">Maximum number of signals to return (default 50).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<RunHealthSignalRecord>> QuerySignalsAsync(
        string? conceptName = null,
        string? agentName = null,
        DateTimeOffset? since = null,
        int limit = 50,
        CancellationToken ct = default);

    /// <summary>
    /// Part 2c (DM-3) — cross-run rollup. Lists recent runs that have at least one persisted
    /// mechanical-failure signal, computing each run's worst level (warning|error) on the fly.
    /// Caveat: runs whose only failures came from the MCP / LLM gateway / graph node stores
    /// (synthesised at read time by the aggregator) will NOT appear here. v1 trades completeness
    /// for cost — a full cross-run rollup requires either pre-computing per-run health on write
    /// or fanning out the aggregator across every recent run.
    /// </summary>
    /// <param name="minLevel">Minimum level to include: <see cref="FailureLevel.Warning"/> for degraded+failed, <see cref="FailureLevel.Error"/> for failed only.</param>
    /// <param name="since">Earliest signal timestamp to consider; null defaults to 24h ago.</param>
    /// <param name="limit">Maximum runs to return (default 50).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<RunHealthRunSummary>> ListDegradedRunsAsync(
        FailureLevel minLevel = FailureLevel.Warning,
        DateTimeOffset? since = null,
        int limit = 50,
        CancellationToken ct = default);
}

/// <summary>
/// Per-run summary row returned by <see cref="IRunHealthStore.ListDegradedRunsAsync"/>.
/// </summary>
/// <param name="RunId">The run id.</param>
/// <param name="WorstLevel">Worst <see cref="FailureLevel"/> across the run's persisted signals.</param>
/// <param name="SignalCount">Number of persisted signals for this run in the queried window.</param>
/// <param name="LatestAt">Timestamp of the most recent signal in the window.</param>
public sealed record RunHealthRunSummary(
    string RunId,
    FailureLevel WorstLevel,
    int SignalCount,
    DateTimeOffset LatestAt);

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
