// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Observability.AgentRunStore;

/// <summary>Represents a single standalone agent invocation recorded by <see cref="IAgentRunStore"/>.</summary>
public sealed record AgentRun(
    string AgentRunId,
    string AgentId,
    AgentRunStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    long? DurationMs,
    string? InputText,
    string? OutputText,
    int InputTokens,
    int OutputTokens,
    string? Error,
    string? CorrelationId,
    string? UserId,
    string? TenantId);
