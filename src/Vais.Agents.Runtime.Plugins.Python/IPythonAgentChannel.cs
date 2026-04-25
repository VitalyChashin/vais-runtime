// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Plugins.Python;

/// <summary>
/// Minimal contract for sending <c>vais/agent.invoke</c> calls to a Python subprocess.
/// Implemented by <see cref="PythonSubprocessSupervisor"/>; extracted as an interface
/// so <see cref="PythonAgentShim"/> can be unit-tested without a running subprocess.
/// </summary>
internal interface IPythonAgentChannel
{
    /// <summary>The descriptor of the plugin this channel connects to.</summary>
    PythonPluginDescriptor Descriptor { get; }

    /// <summary>
    /// Send a <c>vais/agent.invoke</c> call and return the response.
    /// Throws <see cref="InvalidOperationException"/> when the subprocess is unavailable,
    /// and <see cref="TimeoutException"/> on invoke timeout.
    /// </summary>
    Task<AgentInvokeResponse> InvokeAgentAsync(AgentInvokeRequest request, CancellationToken ct);
}
