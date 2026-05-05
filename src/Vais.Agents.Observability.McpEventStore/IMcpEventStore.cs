// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Observability.McpEventStore;

/// <summary>
/// Storage back-end for per-server MCP tool dispatch event history.
/// Written to by <c>McpEventMiddleware</c> after every tool call; exposed via
/// <c>GET /v1/mcp-servers/{id}/events</c>.
/// </summary>
public interface IMcpEventStore
{
    /// <summary>Idempotently creates the required schema. Called once on startup.</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Persists a single MCP tool event.</summary>
    Task RecordAsync(McpEvent evt, CancellationToken ct = default);

    /// <summary>
    /// Returns events for an MCP server, ordered by <c>at DESC</c>.
    /// </summary>
    /// <param name="serverId">Server identifier set at registration time.</param>
    /// <param name="since">Inclusive lower bound on <see cref="McpEvent.At"/>.</param>
    /// <param name="until">Inclusive upper bound on <see cref="McpEvent.At"/>.</param>
    /// <param name="toolName">Filter to a specific tool name; <see langword="null"/> returns all.</param>
    /// <param name="kind">Filter to a specific <see cref="McpEvent.EventKind"/> value; <see langword="null"/> returns all.</param>
    /// <param name="limit">Maximum number of events to return (default 50).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<McpEvent>> ListAsync(string serverId,
        DateTimeOffset? since = null, DateTimeOffset? until = null,
        string? toolName = null, string? kind = null, int limit = 50,
        CancellationToken ct = default);

    /// <summary>Deletes events whose <c>created_at</c> is older than <paramref name="cutoff"/>.</summary>
    Task DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}
