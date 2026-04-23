// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Invokes a remote A2A-compatible agent from a graph node. Parallel to
/// <see cref="IAgentRemoteInvoker"/> but uses the Agent-to-Agent protocol
/// instead of the Vais HTTP control plane. A2A agents are identified by
/// their URL (not id+version), so no <see cref="AgentHandle"/> is needed.
/// </summary>
public interface IA2AGraphNodeInvoker
{
    /// <summary>
    /// Send a text message to the remote A2A agent and return its text response.
    /// </summary>
    /// <param name="a2aUrl">Absolute URL of the remote A2A agent endpoint.</param>
    /// <param name="message">Text payload to send to the remote agent.</param>
    /// <param name="bearerToken">Optional bearer token for outbound authorization.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The remote agent's text response.</returns>
    ValueTask<string> InvokeAsync(
        string a2aUrl,
        string message,
        string? bearerToken,
        CancellationToken cancellationToken = default);
}
