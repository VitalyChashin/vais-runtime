// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// Thrown when a caller attempts to start a run with a <c>RunId</c> that is
/// already in flight for the same graph. HTTP layer maps to 409 with URN
/// <c>urn:vais-agents:graph-run-conflict</c>.
/// </summary>
public sealed class GraphRunConflictException : Exception
{
    /// <summary>Graph identifier the conflict occurred under.</summary>
    public string GraphId { get; }

    /// <summary>Run identifier that is already in flight.</summary>
    public string RunId { get; }

    /// <inheritdoc cref="GraphRunConflictException"/>
    public GraphRunConflictException(string graphId, string runId)
        : base($"A run with id '{runId}' is already active for graph '{graphId}'.")
    {
        GraphId = graphId;
        RunId = runId;
    }
}
