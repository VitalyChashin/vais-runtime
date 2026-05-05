// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Observability.GatewayEventStore;

/// <summary>
/// Storage back-end for per-gateway LLM completion event history.
/// Written to by <c>GatewayEventMiddleware</c> after every completion; exposed via
/// <c>GET /v1/llm-gateways/{id}/events</c>.
/// </summary>
public interface IGatewayEventStore
{
    /// <summary>Idempotently creates the required schema. Called once on startup.</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Persists a single gateway event.</summary>
    Task RecordAsync(GatewayEvent evt, CancellationToken ct = default);

    /// <summary>
    /// Returns events for a gateway, ordered by <c>at DESC</c>.
    /// </summary>
    /// <param name="gatewayId">Gateway identifier set at registration time.</param>
    /// <param name="since">Inclusive lower bound on <see cref="GatewayEvent.At"/>.</param>
    /// <param name="until">Inclusive upper bound on <see cref="GatewayEvent.At"/>.</param>
    /// <param name="kind">Filter to a specific <see cref="GatewayEvent.EventKind"/> value; <see langword="null"/> returns all.</param>
    /// <param name="limit">Maximum number of events to return (default 50).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<GatewayEvent>> ListAsync(string gatewayId,
        DateTimeOffset? since = null, DateTimeOffset? until = null,
        string? kind = null, int limit = 50, CancellationToken ct = default);

    /// <summary>Deletes events whose <c>created_at</c> is older than <paramref name="cutoff"/>.</summary>
    Task DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}
