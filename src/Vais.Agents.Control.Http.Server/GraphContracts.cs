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
