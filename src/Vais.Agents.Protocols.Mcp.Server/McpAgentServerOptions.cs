// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Protocols.Mcp.Server;

/// <summary>
/// Configuration for <c>McpAgentServerBuilder</c>. Consumers override the name /
/// version / instructions advertised to MCP clients; everything else defaults to
/// a sensible production shape.
/// </summary>
public sealed class McpAgentServerOptions
{
    /// <summary>Server name advertised in MCP's <c>initialize</c> handshake. Default <c>"Vais.Agents MCP Server"</c>.</summary>
    public string Name { get; set; } = "Vais.Agents MCP Server";

    /// <summary>Server version advertised in MCP's <c>initialize</c> handshake. Default <c>"0.7"</c>.</summary>
    public string Version { get; set; } = "0.7";

    /// <summary>
    /// Optional free-form instructions sent to MCP clients on handshake. Claude Desktop
    /// and similar surfaces render these to the user; useful to hint "this server
    /// exposes Vais agents — call them via their ids".
    /// </summary>
    public string? Instructions { get; set; }

    /// <summary>
    /// Optional label-prefix filter applied to the registry when enumerating tools.
    /// Null = expose every registered agent. Matches <c>IAgentRegistry.ListAsync</c>'s
    /// prefix argument — useful for multi-tenant deployments where one registry holds
    /// many tenants and a given MCP endpoint should surface a subset.
    /// </summary>
    public string? LabelPrefixFilter { get; set; }
}
