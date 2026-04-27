// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// Thrown when a <see cref="McpServerHandle"/> references a server that is not
/// registered in the current <see cref="IMcpServerRegistry"/>. HTTP layer maps to
/// 404 with URN <c>urn:vais-agents:mcp-server-handle-not-found</c>.
/// </summary>
public sealed class McpServerHandleNotFoundException : Exception
{
    /// <summary>Server identifier that was not found.</summary>
    public string ServerId { get; }

    /// <summary>Version that was not found.</summary>
    public string Version { get; }

    /// <inheritdoc cref="McpServerHandleNotFoundException"/>
    public McpServerHandleNotFoundException(string serverId, string version)
        : base($"McpServer '{serverId}' version '{version}' is not registered.")
    {
        ServerId = serverId;
        Version = version;
    }
}
