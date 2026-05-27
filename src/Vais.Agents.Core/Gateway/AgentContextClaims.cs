// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Core;

/// <summary>
/// Per-call <see cref="AgentContext"/> claims carried in the call-token payload from the
/// runtime to a container plugin and back. Lets a plugin's gateway callback (e.g.
/// <c>POST /v1/container-gateway/llm/complete</c>) reconstruct the same <c>AgentContext</c>
/// the calling grain had in hand — so downstream middleware that reads
/// <see cref="AgentContext.Scopes"/> / <see cref="AgentContext.PrivilegeLevel"/> /
/// <see cref="AgentContext.AllowedTools"/> etc. fires for plugin agents identically to
/// in-process agents (closes the G4 propagation gap).
/// </summary>
/// <remarks>
/// <para>
/// Only the policy-bearing fields travel. <c>AgentName</c> / <c>RunId</c> / lease-id stay
/// in the primary token payload (they're identity, not policy). <c>AgentContext</c> fields
/// that no policy middleware reads today (e.g. transient activity tags) are excluded.
/// </para>
/// <para>
/// Wire format is <c>base64url(JSON)</c> appended as the middle segment of a v3 call-token:
/// <c>base64url(payload).base64url(claims-json).base64url(hmac)</c>. The HMAC covers the
/// payload + claims-json, so claims tampering is detected. Old v2 tokens (two segments,
/// no claims) parse with a <c>null</c> claims field — backwards-compatible during rollout.
/// </para>
/// </remarks>
public sealed record AgentContextClaims(
    string? UserId,
    string? TenantId,
    string? CorrelationId,
    string? WorkspaceId,
    PrivilegeLevel? PrivilegeLevel,
    AutonomyLevel? AutonomyLevel,
    IReadOnlyList<string>? Scopes,
    IReadOnlyList<string>? AllowedTools,
    int? MaxChainDepth,
    string? BaselineRunId)
{
    /// <summary>
    /// All-null instance. Useful when the calling grain has no per-call context to forward
    /// (e.g. an unauthenticated dev-mode invocation); a token carrying this is semantically
    /// equivalent to a legacy two-segment token.
    /// </summary>
    public static AgentContextClaims Empty { get; } = new(
        UserId: null, TenantId: null, CorrelationId: null, WorkspaceId: null,
        PrivilegeLevel: null, AutonomyLevel: null,
        Scopes: null, AllowedTools: null,
        MaxChainDepth: null, BaselineRunId: null);

    /// <summary>
    /// Project an <see cref="AgentContext"/> onto its policy-bearing subset. Used at
    /// call-token mint time to capture the grain's view of the call. <see cref="AgentContext.AllowedTools"/>
    /// is a set; serialized as a stable-ordered list so the wire form round-trips deterministically
    /// (the consumer rebuilds the set).
    /// </summary>
    public static AgentContextClaims From(AgentContext context) =>
        new(
            UserId: context.UserId,
            TenantId: context.TenantId,
            CorrelationId: context.CorrelationId,
            WorkspaceId: context.WorkspaceId,
            PrivilegeLevel: context.PrivilegeLevel,
            AutonomyLevel: context.AutonomyLevel,
            Scopes: context.Scopes,
            AllowedTools: context.AllowedTools is null ? null : [.. context.AllowedTools],
            MaxChainDepth: context.MaxChainDepth,
            BaselineRunId: context.BaselineRunId);
}
