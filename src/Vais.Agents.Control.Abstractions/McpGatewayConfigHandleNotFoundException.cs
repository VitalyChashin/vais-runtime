// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// Thrown when a <see cref="McpGatewayConfigHandle"/> references a config that is not
/// registered in the current <see cref="IMcpGatewayConfigRegistry"/>. HTTP layer maps to
/// 404 with URN <c>urn:vais-agents:mcp-gateway-config-handle-not-found</c>.
/// </summary>
public sealed class McpGatewayConfigHandleNotFoundException : Exception
{
    /// <summary>Config identifier that was not found.</summary>
    public string ConfigId { get; }

    /// <summary>Version that was not found.</summary>
    public string Version { get; }

    /// <inheritdoc cref="McpGatewayConfigHandleNotFoundException"/>
    public McpGatewayConfigHandleNotFoundException(string configId, string version)
        : base($"McpGatewayConfig '{configId}' version '{version}' is not registered.")
    {
        ConfigId = configId;
        Version = version;
    }
}
