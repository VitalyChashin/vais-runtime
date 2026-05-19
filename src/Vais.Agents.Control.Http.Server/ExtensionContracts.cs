// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json.Serialization;

namespace Vais.Agents.Control.Http;

/// <summary>Outcome of a <c>POST /v1/extensions</c> apply request.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExtensionApplyStatus
{
    /// <summary>Extension updated — an older version was replaced.</summary>
    Success = 0,
    /// <summary>Extension loaded for the first time (no prior version).</summary>
    Created = 1,
    /// <summary>DLL could not be loaded (bad IL, missing deps, or IO error).</summary>
    LoadFailed = 2,
    /// <summary><c>[VaisExtension].TargetApiVersion</c> does not match the runtime ABI.</summary>
    AbiMismatch = 3,
    /// <summary>Two handlers on the same seam share the same priority in the same scope.</summary>
    PriorityConflict = 4,
    /// <summary>Manifest was missing or could not be parsed.</summary>
    ValidationFailed = 5,
}

/// <summary>Response body for <c>POST /v1/extensions</c>.</summary>
public sealed record ExtensionApplyResponse(
    string ExtensionId,
    ExtensionApplyStatus Status,
    IReadOnlyList<string>? Handlers,
    string? ErrorMessage);

/// <summary>Outcome of a <c>DELETE /v1/extensions/{name}</c> request.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExtensionDeleteStatus
{
    /// <summary>Extension removed from registry and ALC unloaded.</summary>
    Success = 0,
    /// <summary>No extension with the given id was loaded — nothing to unload.</summary>
    NotFound = 1,
}

/// <summary>Response body for <c>DELETE /v1/extensions/{name}</c>.</summary>
public sealed record ExtensionDeleteResponse(
    string ExtensionId,
    ExtensionDeleteStatus Status);

// ── EXT-25 / EXT-28 list + query + diagnostic types ─────────────────────────

/// <summary>Summary of one handler within a loaded extension.</summary>
public sealed record ExtensionHandlerInfo(
    string HandlerId,
    string Seam,
    int Priority,
    string FailureMode);

/// <summary>Summary of a loaded extension for <c>GET /v1/extensions</c>.</summary>
public sealed record ExtensionInfo(
    string ExtensionId,
    string Version,
    string Host,
    IReadOnlyList<ExtensionHandlerInfo> Handlers);

/// <summary>Response body for <c>GET /v1/extensions</c>.</summary>
public sealed record ExtensionListResponse(IReadOnlyList<ExtensionInfo> Items);

/// <summary>Response body for <c>GET /v1/extensions/{name}</c>.</summary>
public sealed record ExtensionQueryResponse(
    ExtensionInfo Extension,
    ExtensionManifest Manifest);

/// <summary>
/// One handler entry in the per-agent extension chain diagnostic returned by
/// <c>GET /v1/agents/{id}/extensions</c>.
/// </summary>
public sealed record AgentExtensionEntry(
    string ExtensionId,
    string HandlerId,
    string Seam,
    int Priority,
    string FailureMode,
    bool MatchedScope,
    string? ScopeSummary);

/// <summary>Response body for <c>GET /v1/agents/{id}/extensions</c>.</summary>
public sealed record AgentExtensionChainResponse(
    string AgentId,
    IReadOnlyList<AgentExtensionEntry> Handlers);
