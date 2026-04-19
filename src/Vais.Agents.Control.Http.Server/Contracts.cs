// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Http;

/// <summary>
/// Response body for <c>GET /v1/agents/{id}</c>. Pairs the stored
/// <see cref="AgentManifest"/> with the runtime <see cref="AgentHandle"/> +
/// <see cref="AgentStatus"/> so consumers get a single round-trip view of "what's
/// registered" and "what it's doing right now".
/// </summary>
public sealed record AgentQueryResponse(
    AgentManifest Manifest,
    AgentHandle Handle,
    AgentStatus Status);

/// <summary>
/// Response body for <c>GET /v1/agents</c>. Carries the page plus an opaque cursor
/// for the next page (null when at end).
/// </summary>
public sealed record AgentListResponse(
    IReadOnlyList<AgentManifest> Items,
    string? NextCursor = null);
