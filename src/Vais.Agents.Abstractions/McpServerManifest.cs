// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Registered MCP server — physical or virtual. Physical: wraps a single upstream
/// server (same fields as <see cref="McpServerRef"/> plus registry metadata). Virtual:
/// aggregates N upstream registered servers behind one logical name with optional
/// tool projection (IBM Context Forge "virtual server" concept).
/// </summary>
/// <remarks>
/// Agents reference registered servers by setting <c>McpServerRef.Transport = "registered"</c>
/// and <c>McpServerRef.Name</c> to this manifest's <see cref="Id"/>. The
/// <c>AgentManifestTranslator</c> expands the ref at grain activation.
/// </remarks>
public sealed record McpServerManifest(
    string Id,
    string Version,
    string? Description = null,
    IReadOnlyDictionary<string, string>? Labels = null)
{
    // ── Physical server fields (mirror McpServerRef) ──────────────────────────

    /// <summary>Transport: "streamableHttp" | "sse" | "stdio". Required for physical servers.</summary>
    public string? Transport { get; init; }

    /// <summary>Server URL for <c>streamableHttp</c> / <c>sse</c> transports.</summary>
    public string? Url { get; init; }

    /// <summary>Executable path for <c>stdio</c> transport.</summary>
    public string? Command { get; init; }

    /// <summary>Command-line arguments for <c>stdio</c> transport.</summary>
    public IReadOnlyList<string>? Args { get; init; }

    /// <summary>Environment variables passed to <c>stdio</c> child processes.</summary>
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    /// <summary>Optional <c>secret://</c> URI for bearer / header auth on HTTP transports.</summary>
    public string? AuthRef { get; init; }

    /// <summary>Optional tool allowlist. Null = expose all tools the server lists.</summary>
    public IReadOnlyList<string>? Tools { get; init; }

    // ── Virtual server fields ─────────────────────────────────────────────────

    /// <summary>True = virtual aggregator. False (default) = physical server.</summary>
    public bool Virtual { get; init; }

    /// <summary>Upstream server refs for virtual mode. Each <c>Ref</c> is an <see cref="Id"/> in <see cref="IMcpServerRegistry"/>.</summary>
    public IReadOnlyList<McpServerSourceRef>? Sources { get; init; }

    /// <summary>
    /// Tool projection for virtual mode. Maps visible tool name → source server id.
    /// Null = expose all tools from all sources (de-duplicated by name, first source wins).
    /// </summary>
    public IReadOnlyList<McpServerToolProjection>? ToolProjection { get; init; }

    // ── Governance ────────────────────────────────────────────────────────────

    /// <summary>
    /// Optional <see cref="McpGatewayConfigManifest.Id"/> applied per tool dispatch through this server.
    /// Lower precedence than an agent-level <see cref="AgentManifest.McpGatewayRef"/>.
    /// </summary>
    public string? McpGatewayRef { get; init; }

    /// <summary>Free-form operator-visible metadata.</summary>
    public IReadOnlyDictionary<string, string>? Annotations { get; init; }
}

/// <summary>Reference to an upstream registered <see cref="McpServerManifest"/> within a virtual server's <c>Sources</c> list.</summary>
/// <param name="Ref">The <see cref="McpServerManifest.Id"/> of the upstream server.</param>
public sealed record McpServerSourceRef(string Ref);

/// <summary>Maps a visible tool name to the source server in a virtual server's projection.</summary>
/// <param name="Name">The tool name as exposed to the agent.</param>
/// <param name="From">The <see cref="McpServerManifest.Id"/> of the source server that provides this tool.</param>
/// <param name="SourceToolName">Optional override for the upstream tool name if it differs from <see cref="Name"/>.</param>
public sealed record McpServerToolProjection(string Name, string From, string? SourceToolName = null);

/// <summary>Stable identity reference to a registered <see cref="McpServerManifest"/>.</summary>
public sealed record McpServerHandle(string Id, string Version);

/// <summary>Runtime status snapshot returned by <c>IMcpServerLifecycleManager.QueryAsync</c>.</summary>
public sealed record McpServerStatus(McpServerHandle Handle, bool Virtual, DateTimeOffset RegisteredAt);
