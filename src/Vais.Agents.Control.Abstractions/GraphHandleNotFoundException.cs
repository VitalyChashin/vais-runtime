// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// Thrown when an <see cref="AgentGraphHandle"/> references a graph that is not
/// registered in the current <see cref="IAgentGraphRegistry"/>. HTTP layer maps to
/// 404 with URN <c>urn:vais-agents:graph-handle-not-found</c>.
/// </summary>
public sealed class GraphHandleNotFoundException : Exception
{
    /// <summary>Graph identifier that was not found.</summary>
    public string GraphId { get; }

    /// <summary>Version that was not found.</summary>
    public string Version { get; }

    /// <inheritdoc cref="GraphHandleNotFoundException"/>
    public GraphHandleNotFoundException(string graphId, string version)
        : base($"Graph '{graphId}' version '{version}' is not registered.")
    {
        GraphId = graphId;
        Version = version;
    }
}
