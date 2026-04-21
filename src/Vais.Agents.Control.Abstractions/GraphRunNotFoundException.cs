// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// Thrown when a run-id is supplied to a verb (resume, cancel) but no run with
/// that id exists for the given graph handle. HTTP layer maps to 404 with URN
/// <c>urn:vais-agents:graph-run-not-found</c>.
/// </summary>
public sealed class GraphRunNotFoundException : Exception
{
    /// <summary>Graph identifier the run was expected under.</summary>
    public string GraphId { get; }

    /// <summary>Run identifier that was not found.</summary>
    public string RunId { get; }

    /// <inheritdoc cref="GraphRunNotFoundException"/>
    public GraphRunNotFoundException(string graphId, string runId)
        : base($"No run '{runId}' found for graph '{graphId}'.")
    {
        GraphId = graphId;
        RunId = runId;
    }
}
