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
