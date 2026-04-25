// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Http;

/// <summary>
/// Response body for <c>POST /v1/agents</c> (Create) and <c>PATCH /v1/agents/{id}</c> (Update).
/// Pairs the <see cref="AgentHandle"/> with any non-fatal diagnostics the manifest
/// translator emitted during the apply flow. <see cref="Warnings"/> is always present
/// (never null) — an empty list means the apply was clean.
/// </summary>
public sealed record AgentApplyResponse(
    AgentHandle Handle,
    IReadOnlyList<ApplyDiagnostic> Warnings);

/// <summary>
/// A non-fatal warning emitted by the manifest translator during an apply or update flow.
/// The agent was accepted; the warning signals that something may not behave as expected
/// (e.g. both a plugin handler and declarative <c>Model</c> fields were set — plugin wins,
/// declarative fields are silently ignored).
/// </summary>
public sealed record ApplyDiagnostic(string Urn, string Detail);

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
