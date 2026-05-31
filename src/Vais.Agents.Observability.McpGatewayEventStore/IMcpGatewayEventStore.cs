// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Observability.McpGatewayEventStore;

/// <summary>
/// Storage back-end for per-gateway MCP tool dispatch event history.
/// Written to by <c>McpGatewayEventMiddleware</c> after every tool call; exposed via
/// <c>GET /v1/mcp-gateways/{id}/events</c>.
/// </summary>
public interface IMcpGatewayEventStore
{
    /// <summary>Idempotently creates the required schema. Called once on startup.</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Persists a single MCP gateway tool event.</summary>
    Task RecordAsync(McpGatewayEvent evt, CancellationToken ct = default);

    /// <summary>
    /// Returns events for an MCP gateway, ordered by <c>at DESC</c>.
    /// </summary>
    /// <param name="gatewayId">Gateway identifier set at registration time.</param>
    /// <param name="since">Inclusive lower bound on <see cref="McpGatewayEvent.At"/>.</param>
    /// <param name="until">Inclusive upper bound on <see cref="McpGatewayEvent.At"/>.</param>
    /// <param name="toolName">Filter to a specific tool name; <see langword="null"/> returns all.</param>
    /// <param name="kind">Filter to a specific <see cref="McpGatewayEvent.EventKind"/> value; <see langword="null"/> returns all.</param>
    /// <param name="limit">Maximum number of events to return (default 50).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<McpGatewayEvent>> ListAsync(string gatewayId,
        DateTimeOffset? since = null, DateTimeOffset? until = null,
        string? toolName = null, string? kind = null, int limit = 50,
        CancellationToken ct = default);

    /// <summary>
    /// Returns events for a specific run (ordered by <c>at DESC</c>), using the <c>run_id</c> index.
    /// Powers the run-health rollup, which attributes failing tool calls to a run rather than to a
    /// gateway. Returns an empty list when the run produced no MCP gateway events.
    /// </summary>
    /// <param name="runId">Run identifier stamped on each event from the ambient agent context.</param>
    /// <param name="limit">Maximum number of events to return (default 200).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<McpGatewayEvent>> ListByRunAsync(string runId, int limit = 200, CancellationToken ct = default);

    /// <summary>Deletes events whose <c>created_at</c> is older than <paramref name="cutoff"/>.</summary>
    Task DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default);

    /// <summary>
    /// Part 2c (DM-4) — cross-run, cross-gateway search for failed MCP tool calls. Used by
    /// <c>vais.failures concept=McpToolError</c> on the diagnostic MCP surface. Returns the
    /// most recent failed events (<c>error_type IS NOT NULL</c>) matching the optional filters.
    /// </summary>
    /// <param name="toolName">Filter to a specific tool name; null returns all.</param>
    /// <param name="since">Earliest <c>at</c> timestamp; null means no lower bound.</param>
    /// <param name="limit">Maximum number of events to return (default 50).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<McpGatewayEvent>> QueryFailedAcrossGatewaysAsync(
        string? toolName = null,
        DateTimeOffset? since = null,
        int limit = 50,
        CancellationToken ct = default);
}
