// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Declarative specification for a named MCP/tool gateway pipeline.
/// Stored in <see cref="IMcpGatewayConfigRegistry"/>; referenced from
/// <see cref="AgentManifest.McpGatewayRef"/> or
/// <see cref="McpServerManifest.McpGatewayRef"/>.
/// </summary>
public sealed record McpGatewayConfigManifest(
    string Id,
    string Version,
    IReadOnlyList<GatewayMiddlewareSpec> Middleware,
    string? Description = null,
    IReadOnlyDictionary<string, string>? Labels = null)
{
    /// <summary>
    /// Per-workspace allow/deny tool policies. Keys are <c>AgentContext.WorkspaceId</c> values.
    /// Absent workspace → no policy constraint beyond the middleware chain.
    /// </summary>
    public IReadOnlyDictionary<string, McpWorkspacePolicySpec>? WorkspacePolicies { get; init; }

    /// <summary>Free-form operator-visible metadata.</summary>
    public IReadOnlyDictionary<string, string>? Annotations { get; init; }
}

/// <summary>
/// Allow/deny tool policy for a specific workspace. Applied by
/// <c>ToolWorkspacePolicyMiddleware</c> when a
/// <see cref="McpGatewayConfigManifest.WorkspacePolicies"/> entry exists for the
/// current <c>AgentContext.WorkspaceId</c>.
/// </summary>
public sealed record McpWorkspacePolicySpec(
    IReadOnlyList<string>? AllowedTools = null,
    IReadOnlyList<string>? DeniedTools = null,
    int MinPrivilegeLevel = 0);

/// <summary>Stable identity reference to a registered <see cref="McpGatewayConfigManifest"/>.</summary>
public sealed record McpGatewayConfigHandle(string Id, string Version);

/// <summary>Runtime status snapshot returned by <c>IMcpGatewayConfigLifecycleManager.QueryAsync</c>.</summary>
public sealed record McpGatewayConfigStatus(McpGatewayConfigHandle Handle, DateTimeOffset RegisteredAt);
