// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// Thrown when an <see cref="AgentHandle"/> references an agent that is not
/// registered in the current <see cref="IAgentRegistry"/>. HTTP layer maps to
/// 404 with URN <c>urn:vais-agents:agent-handle-not-found</c>.
/// </summary>
public sealed class AgentHandleNotFoundException : Exception
{
    /// <summary>Agent identifier that was not found.</summary>
    public string AgentId { get; }

    /// <summary>Version that was not found.</summary>
    public string Version { get; }

    /// <inheritdoc cref="AgentHandleNotFoundException"/>
    public AgentHandleNotFoundException(string agentId, string version)
        : base($"Agent '{agentId}' version '{version}' is not registered.")
    {
        AgentId = agentId;
        Version = version;
    }
}
