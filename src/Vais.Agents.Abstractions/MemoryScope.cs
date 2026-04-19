// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Partition key for <see cref="IMemoryStore"/> operations. Items written under one
/// scope are invisible to reads under a different scope — stores are expected to
/// partition by the full record.
/// </summary>
/// <remarks>
/// All fields are optional. A scope with no fields set represents a global partition
/// (shared across sessions / agents / tenants). Populate fields to narrow the blast
/// radius: <see cref="SessionId"/> for per-conversation memory,
/// <see cref="AgentId"/> for per-agent, <see cref="TenantId"/> for multi-tenant
/// isolation.
/// </remarks>
/// <param name="SessionId">Optional session identifier; partitions per conversation.</param>
/// <param name="AgentId">Optional agent identifier; partitions per agent.</param>
/// <param name="TenantId">Optional tenant identifier; partitions per tenant.</param>
/// <param name="Durability">Intended durability class. Default: <see cref="MemoryDurability.LongTerm"/>.</param>
public sealed record MemoryScope(
    string? SessionId = null,
    string? AgentId = null,
    string? TenantId = null,
    MemoryDurability Durability = MemoryDurability.LongTerm);
