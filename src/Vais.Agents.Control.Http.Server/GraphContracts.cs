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
