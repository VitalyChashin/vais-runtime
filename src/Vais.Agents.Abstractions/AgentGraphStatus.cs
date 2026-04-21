// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Snapshot of a deployed graph's operational counters at query time.
/// Returned by <c>IAgentGraphLifecycleManager.QueryAsync</c>.
/// </summary>
/// <param name="GraphId">Graph identifier.</param>
/// <param name="Version">Version of the deployed manifest.</param>
/// <param name="ActiveRunCount">Number of runs currently executing (not yet complete or interrupted).</param>
/// <param name="CompletedRunCount">Cumulative completed runs since deployment.</param>
/// <param name="PendingInterruptCount">Runs paused at an <c>Interrupt</c> node awaiting resume.</param>
/// <param name="LastInvokedAt">UTC timestamp of the most recent invocation, or <see langword="null"/> if never invoked.</param>
public sealed record AgentGraphStatus(
    string GraphId,
    string Version,
    int ActiveRunCount,
    int CompletedRunCount,
    int PendingInterruptCount,
    DateTimeOffset? LastInvokedAt);
