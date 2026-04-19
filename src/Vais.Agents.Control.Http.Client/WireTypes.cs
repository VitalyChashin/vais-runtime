// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Http;

/// <summary>
/// Client-side wire type for <c>GET /v1/agents/{id}</c>. Mirrors the server's
/// equivalent shape in <c>Vais.Agents.Control.Http.Server</c> — the client
/// package re-declares the shape instead of depending on the server package,
/// so HTTP consumers (UIs, CLIs, tests) don't pull ASP.NET Core at reference time.
/// </summary>
public sealed record AgentQueryResponse(
    AgentManifest Manifest,
    AgentHandle Handle,
    AgentStatus Status);

/// <summary>Client-side wire type for paged list responses.</summary>
public sealed record AgentListResponse(
    IReadOnlyList<AgentManifest> Items,
    string? NextCursor = null);
