// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Plugins.Python.Tests;

/// <summary>
/// In-memory <see cref="ISubprocessHandle"/> backed by pipe streams.
/// Used by supervisor unit tests — no real process is spawned.
/// </summary>
internal sealed class FakeSubprocessHandle : ISubprocessHandle
{
    private readonly TaskCompletionSource _exitedTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int ProcessId { get; }
    public Stream StandardInput { get; }
    public Stream StandardOutput { get; }
    public TextReader StandardError { get; }
    public Task Exited => _exitedTcs.Task;

    /// <param name="supervisorInput">The stream the supervisor WRITES MCP requests to.</param>
    /// <param name="supervisorOutput">The stream the supervisor READS MCP responses from.</param>
    /// <param name="pid">Simulated process ID (default <c>42</c>).</param>
    /// <param name="stderrLines">Lines emitted on stderr (default: empty).</param>
    internal FakeSubprocessHandle(
        Stream supervisorInput,
        Stream supervisorOutput,
        int pid = 42,
        string? stderrLines = null)
    {
        ProcessId = pid;
        StandardInput = supervisorInput;
        StandardOutput = supervisorOutput;
        StandardError = new StringReader(stderrLines ?? string.Empty);
    }

    /// <summary>Simulate the subprocess exiting.</summary>
    internal void SimulateExit() => _exitedTcs.TrySetResult();

    public void Kill() => SimulateExit();

    public ValueTask DisposeAsync()
    {
        SimulateExit();
        return ValueTask.CompletedTask;
    }
}
