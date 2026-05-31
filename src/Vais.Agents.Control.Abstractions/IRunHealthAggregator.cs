// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// Rolls a whole run tree (the root run plus every agent-as-tool descendant and background sub-run)
/// up into a single <see cref="RunHealth"/>. Composes the durable run-health signal store, the
/// gateway/MCP event stores (queried by run), the graph run store, and the background-run tracker —
/// so a run that "completed" still reveals every recovered or fatal mechanical failure beneath it.
/// </summary>
/// <remarks>
/// The interface lives in <c>Vais.Agents.Control.Abstractions</c> so consumers in
/// <c>Vais.Agents.Control.Mcp.Server</c> (and any other control-plane host) can resolve it from DI
/// without taking a project-reference on <c>Vais.Agents.Control.Http.Server</c>, where the
/// implementation lives. Foundation Part 2 originally shipped the interface alongside the impl in
/// Http.Server; Part 2c (ontology-grounded diagnose MCP) moved the interface here so the
/// <c>/design-mcp</c> handlers can call it directly.
/// </remarks>
public interface IRunHealthAggregator
{
    /// <summary>Computes the health rollup for the run tree rooted at <paramref name="rootRunId"/>.</summary>
    Task<RunHealth> GetRunHealthAsync(string rootRunId, CancellationToken ct = default);

    /// <summary>
    /// Part 2c (DM-3) — cross-run rollup. Returns recent runs whose worst-level signal is at
    /// least <paramref name="minLevel"/>. v1 indexes only bus-sourced signals (the same ones
    /// the run-health store persists); runs whose only failures are from the MCP / LLM gateway
    /// / graph node stores are NOT yet enumerated cross-run.
    /// </summary>
    /// <param name="minLevel">Minimum severity to include: <c>warning</c> for degraded+failed, <c>error</c> for failed only.</param>
    /// <param name="since">Earliest signal timestamp; null defaults to 24h ago.</param>
    /// <param name="limit">Maximum runs to return (default 50, max 200).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<RunHealthListItem>> ListDegradedRunsAsync(
        FailureLevel minLevel = FailureLevel.Warning,
        DateTimeOffset? since = null,
        int limit = 50,
        CancellationToken ct = default);
}

/// <summary>One run summary returned by <see cref="IRunHealthAggregator.ListDegradedRunsAsync"/>.</summary>
/// <param name="RunId">The run id.</param>
/// <param name="Level">Run-level health: <c>healthy</c>, <c>degraded</c>, or <c>failed</c>.</param>
/// <param name="SignalCount">Number of persisted bus-sourced signals for this run in the queried window.</param>
/// <param name="LatestAt">Timestamp of the most recent signal in the window.</param>
public sealed record RunHealthListItem(
    string RunId,
    string Level,
    int SignalCount,
    DateTimeOffset LatestAt);
