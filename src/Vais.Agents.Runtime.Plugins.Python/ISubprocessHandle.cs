// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Plugins.Python;

/// <summary>
/// Represents a running subprocess whose stdin/stdout carry the MCP stdio protocol.
/// Abstracted for unit-testing the supervisor state machine with in-memory pipe streams.
/// </summary>
internal interface ISubprocessHandle : IAsyncDisposable
{
    /// <summary>OS process ID, or <c>-1</c> for in-memory fakes.</summary>
    int ProcessId { get; }

    /// <summary>
    /// The supervisor writes MCP request frames here (maps to the subprocess's stdin).
    /// </summary>
    Stream StandardInput { get; }

    /// <summary>
    /// The supervisor reads MCP response frames from here (maps to the subprocess's stdout).
    /// </summary>
    Stream StandardOutput { get; }

    /// <summary>Subprocess stderr, forwarded line-by-line through <c>ILogger</c>.</summary>
    TextReader StandardError { get; }

    /// <summary>Completes when the subprocess has exited (for any reason).</summary>
    Task Exited { get; }

    /// <summary>Immediately terminates the subprocess.</summary>
    void Kill();
}
