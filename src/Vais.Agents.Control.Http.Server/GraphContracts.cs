// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Http;

/// <summary>Response body for <c>GET /v1/graphs/{id}</c>. Pairs the manifest with current lifecycle status.</summary>
public sealed record AgentGraphQueryResponse(
    AgentGraphManifest Manifest,
    AgentGraphHandle Handle,
    AgentGraphStatus Status);

/// <summary>Response body for <c>GET /v1/graphs</c>. Carries the page plus an opaque next-page cursor.</summary>
public sealed record AgentGraphListResponse(
    IReadOnlyList<AgentGraphManifest> Items,
    string? NextCursor = null);

/// <summary>
/// Response body for <c>POST /v1/graphs/validate</c> (v0.38). Returned for all
/// syntactically-valid requests regardless of whether the manifest passes; use
/// <see cref="Valid"/> to drive exit-code decisions.
/// </summary>
/// <param name="Valid"><c>true</c> when no errors were found (structural + runtime context checks).</param>
/// <param name="Errors">Human-readable error messages, one per violation. Empty when <see cref="Valid"/> is <c>true</c>.</param>
public sealed record GraphValidationResult(bool Valid, IReadOnlyList<string> Errors);

/// <summary>DTO for a single pipeline run returned by run-history endpoints.</summary>
/// <param name="RunId">Unique identifier of the run.</param>
/// <param name="GraphId">Identifier of the graph that produced this run.</param>
/// <param name="Status">Lifecycle status: <c>running</c>, <c>completed</c>, <c>failed</c>, or <c>interrupted</c>.</param>
/// <param name="StartedAt">UTC timestamp when the run was created.</param>
/// <param name="EndedAt">UTC timestamp when the run ended; <see langword="null"/> while running.</param>
/// <param name="DurationMs">Wall-clock duration in milliseconds; <see langword="null"/> while running.</param>
/// <param name="SuperSteps">Number of super-steps executed.</param>
/// <param name="Error">Error message when <paramref name="Status"/> is <c>failed</c>; otherwise <see langword="null"/>.</param>
public sealed record PipelineRunDto(
    string RunId,
    string GraphId,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    long? DurationMs,
    int SuperSteps,
    string? Error);

/// <summary>DTO for a single node execution returned by run-history endpoints.</summary>
/// <param name="RunId">Identifier of the containing run.</param>
/// <param name="NodeId">Identifier of the node within the graph definition.</param>
/// <param name="NodeKind">Registered handler type name.</param>
/// <param name="AgentId">Agent that handled this node; <see langword="null"/> for non-agent nodes.</param>
/// <param name="Status">Lifecycle status: <c>running</c>, <c>completed</c>, <c>failed</c>, or <c>interrupted</c>.</param>
/// <param name="StartedAt">UTC timestamp when this node execution started.</param>
/// <param name="EndedAt">UTC timestamp when this node execution ended; <see langword="null"/> while running.</param>
/// <param name="DurationMs">Wall-clock duration in milliseconds; <see langword="null"/> while running.</param>
/// <param name="InputText">Prompt text passed to the agent; <see langword="null"/> when not recorded.</param>
/// <param name="OutputText">Response text from the agent; <see langword="null"/> when not recorded.</param>
/// <param name="InputTokens">Number of prompt tokens consumed.</param>
/// <param name="OutputTokens">Number of completion tokens generated.</param>
/// <param name="Error">Error detail when <paramref name="Status"/> is <c>failed</c>; otherwise <see langword="null"/>.</param>
/// <param name="EdgesTaken">Edge labels traversed out of this node; <see langword="null"/> when not recorded.</param>
public sealed record NodeExecutionDto(
    string RunId,
    string NodeId,
    string NodeKind,
    string? AgentId,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    long? DurationMs,
    string? InputText,
    string? OutputText,
    int InputTokens,
    int OutputTokens,
    string? Error,
    IReadOnlyList<string>? EdgesTaken);

/// <summary>Response body for <c>GET /v1/graphs/{id}/runs</c>.</summary>
/// <param name="Items">Pipeline runs matching the filter criteria, ordered newest-first.</param>
public sealed record RunListResponse(IReadOnlyList<PipelineRunDto> Items);

/// <summary>DTO for a single LLM gateway completion event returned by <c>GET /v1/llm-gateways/{id}/events</c>.</summary>
/// <param name="EventId">Unique identifier for this event.</param>
/// <param name="GatewayId">Identifier of the gateway that produced this event.</param>
/// <param name="EventKind">Event type: <c>completion.completed</c> or <c>completion.failed</c>.</param>
/// <param name="ModelId">Model identifier; <see langword="null"/> on failure or when not reported.</param>
/// <param name="InputTokens">Prompt token count; 0 when not available.</param>
/// <param name="OutputTokens">Completion token count; 0 when not available.</param>
/// <param name="DurationMs">Wall-clock duration in milliseconds.</param>
/// <param name="CacheHit"><see langword="true"/> when served from a semantic cache; <see langword="null"/> when not applicable.</param>
/// <param name="ErrorType">Exception type name when <paramref name="EventKind"/> is <c>completion.failed</c>; otherwise <see langword="null"/>.</param>
/// <param name="At">UTC timestamp when the event occurred.</param>
/// <param name="CorrelationId">Ambient correlation ID; <see langword="null"/> when not set.</param>
/// <param name="RunId">Graph run ID when the call originated inside a graph run; otherwise <see langword="null"/>.</param>
public sealed record GatewayEventDto(
    string EventId,
    string GatewayId,
    string EventKind,
    string? ModelId,
    int InputTokens,
    int OutputTokens,
    long? DurationMs,
    bool? CacheHit,
    string? ErrorType,
    DateTimeOffset At,
    string? CorrelationId,
    string? RunId);

/// <summary>
/// A single entry in the agent run history list returned by <c>GET /v1/agents/{id}/runs</c>.
/// Represents either a node execution within a graph run (<c>source = "graph"</c>) or a
/// standalone invocation via the invoke endpoints (<c>source = "standalone"</c>).
/// </summary>
/// <param name="RunId">Graph run ID when <paramref name="Source"/> is <c>graph</c>; standalone agent run ID otherwise.</param>
/// <param name="AgentId">Identifier of the agent that handled this invocation.</param>
/// <param name="Source">Origin: <c>graph</c> (part of a graph run) or <c>standalone</c> (direct invoke).</param>
/// <param name="NodeId">Graph node ID; <see langword="null"/> for standalone runs.</param>
/// <param name="NodeKind">Graph node handler type; <see langword="null"/> for standalone runs.</param>
/// <param name="Status">Lifecycle status: <c>running</c>, <c>completed</c>, or <c>failed</c>.</param>
/// <param name="StartedAt">UTC timestamp when this invocation started.</param>
/// <param name="EndedAt">UTC timestamp when this invocation ended; <see langword="null"/> while running.</param>
/// <param name="DurationMs">Wall-clock duration in milliseconds; <see langword="null"/> while running.</param>
/// <param name="InputText">Prompt text; <see langword="null"/> when not recorded.</param>
/// <param name="OutputText">Response text; <see langword="null"/> when not recorded or still running.</param>
/// <param name="InputTokens">Number of prompt tokens consumed.</param>
/// <param name="OutputTokens">Number of completion tokens generated.</param>
/// <param name="Error">Error detail when failed; otherwise <see langword="null"/>.</param>
/// <param name="EdgesTaken">Edge labels traversed (graph runs only); <see langword="null"/> for standalone.</param>
public sealed record AgentRunDto(
    string RunId,
    string AgentId,
    string Source,
    string? NodeId,
    string? NodeKind,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    long? DurationMs,
    string? InputText,
    string? OutputText,
    int InputTokens,
    int OutputTokens,
    string? Error,
    IReadOnlyList<string>? EdgesTaken);

/// <summary>DTO for a single MCP tool-call event returned by <c>GET /v1/mcp-servers/{id}/events</c>.</summary>
/// <param name="EventId">Unique identifier for this event.</param>
/// <param name="ServerId">Identifier of the MCP server that produced this event.</param>
/// <param name="ToolName">Name of the tool that was called.</param>
/// <param name="EventKind">Event type: <c>call.completed</c>, <c>call.failed</c>, <c>call.blocked</c>, or <c>cache.hit</c>.</param>
/// <param name="DurationMs">Wall-clock duration in milliseconds; <see langword="null"/> when not measured.</param>
/// <param name="CacheHit"><see langword="true"/> when served from cache.</param>
/// <param name="BlockedReason">Reason the call was blocked; <see langword="null"/> when not blocked.</param>
/// <param name="ErrorType">Exception type name when <paramref name="EventKind"/> is <c>call.failed</c>; otherwise <see langword="null"/>.</param>
/// <param name="At">UTC timestamp when the event occurred.</param>
/// <param name="CorrelationId">Ambient correlation ID; <see langword="null"/> when not set.</param>
/// <param name="RunId">Graph run ID when the call originated inside a graph run; otherwise <see langword="null"/>.</param>
public sealed record McpEventDto(
    string EventId,
    string ServerId,
    string ToolName,
    string EventKind,
    long? DurationMs,
    bool CacheHit,
    string? BlockedReason,
    string? ErrorType,
    DateTimeOffset At,
    string? CorrelationId,
    string? RunId);
