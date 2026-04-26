// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Http;

/// <summary>
/// Client-side wire type for <c>POST /v1/agents</c> and <c>PATCH /v1/agents/{id}</c>.
/// Mirrors the server's <c>AgentApplyResponse</c> without depending on the server package.
/// </summary>
public sealed record AgentApplyResponse(
    AgentHandle Handle,
    IReadOnlyList<ApplyDiagnostic> Warnings);

/// <summary>
/// Client-side wire type for a non-fatal diagnostic in an apply response.
/// Mirrors the server's <c>ApplyDiagnostic</c>.
/// </summary>
public sealed record ApplyDiagnostic(string Urn, string Detail);

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

/// <summary>
/// Client-side wire type for <c>GET /v1/graphs/{id}</c>. Mirrors the server's
/// <c>AgentGraphQueryResponse</c> without depending on the server package.
/// </summary>
public sealed record AgentGraphQueryResponse(
    AgentGraphManifest Manifest,
    AgentGraphHandle Handle,
    AgentGraphStatus Status);

/// <summary>Client-side wire type for <c>GET /v1/graphs</c>.</summary>
public sealed record AgentGraphListResponse(
    IReadOnlyList<AgentGraphManifest> Items,
    string? NextCursor = null);

/// <summary>
/// Client-side wire type for <c>POST /v1/graphs</c> and <c>PATCH /v1/graphs/{id}</c>.
/// Mirrors the server's <c>AgentGraphApplyResponse</c> without depending on the server package.
/// </summary>
public sealed record AgentGraphApplyResponse(
    AgentGraphHandle Handle,
    IReadOnlyList<ApplyDiagnostic> Warnings);

/// <summary>Client-side wire type for a single entry in <c>GET /v1/runtimes</c>.</summary>
public sealed record RuntimeInfo(string Url, string IdentityMode);

/// <summary>Client-side wire type for <c>GET /v1/runtimes</c>.</summary>
public sealed record RuntimeListResponse(IReadOnlyList<RuntimeInfo> Items);
