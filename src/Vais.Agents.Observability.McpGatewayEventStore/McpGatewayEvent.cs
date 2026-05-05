// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Observability.McpGatewayEventStore;

/// <summary>A single MCP tool dispatch event emitted by the MCP gateway middleware.</summary>
/// <param name="EventId">Unique identifier for this event.</param>
/// <param name="GatewayId">Identifier of the MCP gateway that owns this tool (set at <c>AddMcpGatewayEventStore</c> time).</param>
/// <param name="ToolName">Name of the tool that was dispatched.</param>
/// <param name="EventKind">Event type: <c>call.completed</c> or <c>call.failed</c>.</param>
/// <param name="DurationMs">Wall-clock duration in milliseconds.</param>
/// <param name="CacheHit"><see langword="true"/> when the result was served from a cache; otherwise <see langword="false"/>.</param>
/// <param name="BlockedReason">Reason the call was blocked (e.g. <c>"SecurityFilter"</c>, <c>"DenyList"</c>); <see langword="null"/> when not blocked.</param>
/// <param name="ErrorType">Exception type name when <paramref name="EventKind"/> is <c>call.failed</c>; otherwise <see langword="null"/>.</param>
/// <param name="At">UTC timestamp when the event occurred.</param>
/// <param name="CorrelationId">Ambient correlation ID from the agent context; <see langword="null"/> when not set.</param>
/// <param name="RunId">Graph run ID when the call originated inside a graph run; otherwise <see langword="null"/>.</param>
public sealed record McpGatewayEvent(
    string EventId,
    string GatewayId,
    string ToolName,
    string EventKind,
    long? DurationMs,
    bool CacheHit,
    string? BlockedReason,
    string? ErrorType,
    DateTimeOffset At,
    string? CorrelationId,
    string? RunId);
