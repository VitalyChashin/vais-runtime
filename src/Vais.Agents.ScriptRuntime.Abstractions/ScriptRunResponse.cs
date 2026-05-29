// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.ScriptRuntime;

/// <summary>
/// The result of a single code-mode script execution returned by the ScriptRuntime sidecar.
/// On failure <see cref="Error"/> is populated (never a fabricated success placeholder — P9):
/// the runtime surfaces it as a real tool error, not a silent result.
/// </summary>
public sealed record ScriptRunResponse
{
    /// <summary>Serialized return value of the script (objects JSON-encoded), capped at <c>maxOutputBytes</c>. Null when <see cref="Error"/> is set.</summary>
    public string? Result { get; init; }

    /// <summary>Captured <c>console.*</c> output lines (capped). Forwarded to the structured-log endpoint, not returned to the model by default.</summary>
    public IReadOnlyList<string> Console { get; init; } = [];

    /// <summary>Number of <c>__callTool</c> bridge calls the script issued, for budget accounting and telemetry.</summary>
    public int ToolCallCount { get; init; }

    /// <summary>Set when execution failed (timeout, limit breach, script error, tool-call-cap). Null on success.</summary>
    public ScriptRunError? Error { get; init; }

    /// <summary>Wall-clock execution time in milliseconds.</summary>
    public long WallMs { get; init; }
}

/// <summary>A classified code-mode execution failure.</summary>
/// <param name="Type">Stable failure category — e.g. <c>Timeout</c>, <c>MemoryLimit</c>, <c>StatementLimit</c>, <c>RecursionLimit</c>, <c>ToolCallLimit</c>, <c>ScriptError</c>.</param>
/// <param name="Message">Human-readable detail.</param>
public sealed record ScriptRunError(string Type, string Message);
