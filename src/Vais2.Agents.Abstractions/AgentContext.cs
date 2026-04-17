// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents;

/// <summary>
/// Ambient context for a single agent turn. Read-only snapshot; not thread-shared
/// outside of the turn that created it.
/// </summary>
/// <param name="UserId">Optional user identity driving the turn.</param>
/// <param name="TenantId">Optional tenant / project identity.</param>
/// <param name="CorrelationId">Optional correlation id for cross-service tracing.</param>
/// <param name="AgentName">Optional stable identifier for the agent in use.</param>
public sealed record AgentContext(
    string? UserId = null,
    string? TenantId = null,
    string? CorrelationId = null,
    string? AgentName = null)
{
    /// <summary>An empty context with all fields null. Identity value for defaults.</summary>
    public static readonly AgentContext Empty = new();
}
