// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// Thrown when a <see cref="GraphResumeRequest.InterruptId"/> does not match the
/// interrupt id stored in the checkpoint for the given run. Indicates a stale or
/// incorrect resume payload. HTTP layer maps to 409 with URN
/// <c>urn:vais-agents:graph-interrupt-mismatch</c>.
/// </summary>
public sealed class GraphInterruptMismatchException : Exception
{
    /// <summary>Run identifier the mismatch occurred on.</summary>
    public string RunId { get; }

    /// <summary>Interrupt id supplied by the caller.</summary>
    public string SuppliedInterruptId { get; }

    /// <summary>Interrupt id stored in the checkpoint.</summary>
    public string CheckpointInterruptId { get; }

    /// <inheritdoc cref="GraphInterruptMismatchException"/>
    public GraphInterruptMismatchException(string runId, string suppliedInterruptId, string checkpointInterruptId)
        : base($"Resume for run '{runId}' supplied interrupt id '{suppliedInterruptId}' but checkpoint holds '{checkpointInterruptId}'.")
    {
        RunId = runId;
        SuppliedInterruptId = suppliedInterruptId;
        CheckpointInterruptId = checkpointInterruptId;
    }
}
