// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// Thrown when a resume or cancel is attempted against a run that has already
/// reached a terminal node (<c>End</c> or <c>Failed</c>). HTTP layer maps to 409
/// with URN <c>urn:vais-agents:graph-already-complete</c>.
/// </summary>
public sealed class GraphAlreadyCompleteException : Exception
{
    /// <summary>Run identifier of the completed run.</summary>
    public string RunId { get; }

    /// <inheritdoc cref="GraphAlreadyCompleteException"/>
    public GraphAlreadyCompleteException(string runId)
        : base($"Run '{runId}' has already reached a terminal state and cannot be resumed or cancelled.")
    {
        RunId = runId;
    }
}
