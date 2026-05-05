// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Observability.McpGatewayEventStore;

/// <summary>Configuration options for the Postgres-backed MCP gateway event store.</summary>
public sealed class McpGatewayEventStoreOptions
{
    /// <summary>Postgres connection string.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>
    /// Number of days to retain events. Events older than this are deleted on startup.
    /// Default is 30 days.
    /// </summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>
    /// Identifier written into every <see cref="McpGatewayEvent.GatewayId"/> field.
    /// Set this to the manifest name of the MCP gateway config (e.g. <c>"research-mcp-gateway"</c>)
    /// so that <c>GET /v1/mcp-gateways/{id}/events</c> returns the right events.
    /// Default is <c>"default"</c>.
    /// </summary>
    public string GatewayId { get; set; } = "default";
}
