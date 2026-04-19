// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Protocols.A2A;

/// <summary>
/// Thrown from an A2A-backed tool when the remote agent's response does not
/// contain a usable text payload — empty, an unsupported response kind, or a
/// task that produced no text artifacts. <c>DefaultToolCallDispatcher</c>
/// catches this as a regular tool-throw and surfaces it on
/// <c>ToolCallOutcome.Error</c> so the agent loop feeds the failure back to
/// the model.
/// </summary>
/// <remarks>
/// SDK-level transport failures (HTTP errors, JSON-RPC errors) surface as
/// <c>A2A.A2AException</c> — caught by the dispatcher with the same outcome.
/// </remarks>
public sealed class A2AAgentInvocationException : Exception
{
    /// <summary>The remote agent name (from <c>AgentCard.Name</c>).</summary>
    public string AgentName { get; }

    /// <summary>Construct an exception carrying the agent name + a description of what went wrong.</summary>
    public A2AAgentInvocationException(string agentName, string message)
        : base($"A2A agent '{agentName}' returned an unusable response: {message}")
    {
        AgentName = agentName;
    }
}
