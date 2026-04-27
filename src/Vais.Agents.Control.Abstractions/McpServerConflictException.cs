// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// Thrown when <see cref="IMcpServerLifecycleManager.CreateAsync"/> is called for an
/// (id, version) pair that is already registered. HTTP layer maps to 409 with URN
/// <c>urn:vais-agents:mcp-server-conflict</c>.
/// </summary>
public sealed class McpServerConflictException : Exception
{
    /// <summary>Server identifier that already exists.</summary>
    public string ServerId { get; }

    /// <summary>Version that already exists.</summary>
    public string Version { get; }

    /// <inheritdoc cref="McpServerConflictException"/>
    public McpServerConflictException(string serverId, string version)
        : base($"McpServer '{serverId}' version '{version}' is already registered.")
    {
        ServerId = serverId;
        Version = version;
    }
}
