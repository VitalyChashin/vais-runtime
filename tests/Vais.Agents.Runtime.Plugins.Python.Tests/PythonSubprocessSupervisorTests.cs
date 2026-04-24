// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.IO.Pipelines;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Python.Tests;

/// <summary>
/// State-machine unit tests for <see cref="PythonSubprocessSupervisor"/>.
/// No real processes are spawned — all I/O uses in-memory pipe streams and
/// <see cref="MockMcpResponder"/>.
/// </summary>
public sealed class PythonSubprocessSupervisorTests
{
    // Fast backoff delays to keep test suite quick.
    private static readonly TimeSpan[] FastBackoff =
        [TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20)];

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static PythonPluginDescriptor MakeDescriptor(
        string name = "test-plugin",
        PythonRestartPolicy restartPolicy = PythonRestartPolicy.ExponentialBackoff,
        int handshakeTimeoutSeconds = 5,
        IReadOnlyList<string>? declaredTools = null) =>
        new(
            Name: name,
            PluginDirectory: "/fake",
            InterpreterPath: "/fake/python",
            EntrypointPath: "/fake/server.py",
            TargetApiVersion: "0.23",
            HandshakeTimeoutSeconds: handshakeTimeoutSeconds,
            RestartPolicy: restartPolicy,
            DeclaredTools: declaredTools ?? [],
            SecretRefs: new Dictionary<string, string>());

    private sealed record PipeSetup(
        Stream SupervisorInput,
        Stream SupervisorOutput,
        Stream ResponderInput,
        Stream ResponderOutput);

    private static PipeSetup MakePipes()
    {
        // c2s: supervisor → responder (supervisor writes requests here)
        var c2s = new Pipe();
        // s2c: responder → supervisor (supervisor reads responses from here)
        var s2c = new Pipe();

        return new PipeSetup(
            SupervisorInput: c2s.Writer.AsStream(),
            SupervisorOutput: s2c.Reader.AsStream(),
            ResponderInput: c2s.Reader.AsStream(),
            ResponderOutput: s2c.Writer.AsStream());
    }

    private static async Task PollAsync(
        Func<bool> condition,
        TimeSpan timeout,
        string reason = "condition was not met in time")
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        condition().Should().BeTrue(reason);
    }

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Start_SuccessfulHandshake_StatusReady()
    {
        var pipes = MakePipes();
        var handle = new FakeSubprocessHandle(pipes.SupervisorInput, pipes.SupervisorOutput);
        var responder = new MockMcpResponder(pipes.ResponderInput, pipes.ResponderOutput, ["tool_a"]);

        using var responderCts = new CancellationTokenSource();
        _ = Task.Run(() => responder.RunAsync(responderCts.Token));

        var supervisor = new PythonSubprocessSupervisor(
            MakeDescriptor(declaredTools: ["tool_a"]),
            NullLoggerFactory.Instance,
            _ => handle,
            FastBackoff);

        supervisor.Start();
        var ok = await supervisor.InitialHandshakeTask;

        ok.Should().BeTrue();
        supervisor.Status.Should().Be(PythonPluginStatus.Ready);
        supervisor.ProcessId.Should().Be(42);
        supervisor.McpClient.Should().NotBeNull();

        await responderCts.CancelAsync();
        await supervisor.StopAsync();
    }

    [Fact]
    public async Task Start_HandshakeTimeout_StatusUnavailable()
    {
        var pipes = MakePipes();
        var handle = new FakeSubprocessHandle(pipes.SupervisorInput, pipes.SupervisorOutput);
        // Responder is not started — supervisor gets no MCP response.

        var supervisor = new PythonSubprocessSupervisor(
            MakeDescriptor(handshakeTimeoutSeconds: 1),
            NullLoggerFactory.Instance,
            _ => handle,
            FastBackoff);

        supervisor.Start();
        var ok = await supervisor.InitialHandshakeTask;

        ok.Should().BeFalse();
        supervisor.Status.Should().Be(PythonPluginStatus.Unavailable);
        supervisor.McpClient.Should().BeNull();
    }

    [Fact]
    public async Task Start_HandleFactoryThrows_StatusUnavailable()
    {
        var supervisor = new PythonSubprocessSupervisor(
            MakeDescriptor(),
            NullLoggerFactory.Instance,
            _ => throw new InvalidOperationException("spawn failed"),
            FastBackoff);

        supervisor.Start();
        var ok = await supervisor.InitialHandshakeTask;

        ok.Should().BeFalse();
        supervisor.Status.Should().Be(PythonPluginStatus.Unavailable);
    }

    [Fact]
    public async Task ProcessExit_ExponentialBackoff_RestartsAndReady()
    {
        // Two rounds of pipes: first process exits, second succeeds.
        var pipes1 = MakePipes();
        var pipes2 = MakePipes();

        FakeSubprocessHandle? firstHandle = null;
        var callCount = 0;

        FakeSubprocessHandle HandleFactory(PythonPluginDescriptor _)
        {
            callCount++;
            if (callCount == 1)
            {
                firstHandle = new FakeSubprocessHandle(pipes1.SupervisorInput, pipes1.SupervisorOutput, pid: 100);
                return firstHandle;
            }
            return new FakeSubprocessHandle(pipes2.SupervisorInput, pipes2.SupervisorOutput, pid: 101);
        }

        using var responderCts = new CancellationTokenSource();
        var r1 = new MockMcpResponder(pipes1.ResponderInput, pipes1.ResponderOutput);
        var r2 = new MockMcpResponder(pipes2.ResponderInput, pipes2.ResponderOutput);
        _ = Task.Run(() => r1.RunAsync(responderCts.Token));
        _ = Task.Run(() => r2.RunAsync(responderCts.Token));

        var supervisor = new PythonSubprocessSupervisor(
            MakeDescriptor(restartPolicy: PythonRestartPolicy.ExponentialBackoff),
            NullLoggerFactory.Instance,
            HandleFactory,
            FastBackoff);

        supervisor.Start();
        await supervisor.InitialHandshakeTask;

        supervisor.Status.Should().Be(PythonPluginStatus.Ready);
        supervisor.ProcessId.Should().Be(100);

        // Simulate crash
        firstHandle!.SimulateExit();

        // Supervisor should restart and become Ready with the second handle.
        await PollAsync(
            () => supervisor.Status == PythonPluginStatus.Ready && supervisor.ProcessId == 101,
            timeout: TimeSpan.FromSeconds(5),
            reason: "supervisor should restart and become Ready with pid 101");

        supervisor.McpClient.Should().NotBeNull();
        callCount.Should().Be(2);

        await responderCts.CancelAsync();
        await supervisor.StopAsync();
    }

    [Fact]
    public async Task ProcessExit_RestartPolicyNever_StatusUnavailable()
    {
        var pipes = MakePipes();
        var handle = new FakeSubprocessHandle(pipes.SupervisorInput, pipes.SupervisorOutput);
        var responder = new MockMcpResponder(pipes.ResponderInput, pipes.ResponderOutput);

        using var responderCts = new CancellationTokenSource();
        _ = Task.Run(() => responder.RunAsync(responderCts.Token));

        var supervisor = new PythonSubprocessSupervisor(
            MakeDescriptor(restartPolicy: PythonRestartPolicy.Never),
            NullLoggerFactory.Instance,
            _ => handle,
            FastBackoff);

        supervisor.Start();
        await supervisor.InitialHandshakeTask;

        handle.SimulateExit();

        await PollAsync(
            () => supervisor.Status == PythonPluginStatus.Unavailable,
            timeout: TimeSpan.FromSeconds(3),
            reason: "Never restart policy should transition to Unavailable on exit");

        await responderCts.CancelAsync();
        await supervisor.StopAsync();
    }

    [Fact]
    public async Task ProcessExit_AllRestartsExhausted_StatusUnavailable()
    {
        // 4 pipes: initial + 3 restarts. Each process exits immediately after becoming Ready.
        const int Total = 4;
        var pipesList = Enumerable.Range(0, Total).Select(_ => MakePipes()).ToArray();
        var handles = new List<FakeSubprocessHandle>(Total);
        var responderCts = new CancellationTokenSource();

        foreach (var p in pipesList)
        {
            var h = new FakeSubprocessHandle(p.SupervisorInput, p.SupervisorOutput);
            handles.Add(h);
            var r = new MockMcpResponder(p.ResponderInput, p.ResponderOutput);
            _ = Task.Run(() => r.RunAsync(responderCts.Token));
        }

        var spawnCount = 0;
        var supervisor = new PythonSubprocessSupervisor(
            MakeDescriptor(restartPolicy: PythonRestartPolicy.ExponentialBackoff),
            NullLoggerFactory.Instance,
            _ => handles[spawnCount++],
            FastBackoff);

        supervisor.Start();
        await supervisor.InitialHandshakeTask;

        // Trigger exits for the initial process + each restart (3 exits total exhaust backoff).
        // Wait for spawnCount > i to ensure the i-th handle has actually been spawned (and is
        // Ready) before triggering its exit — prevents the poll from seeing the previous Ready.
        for (var i = 0; i < Total - 1; i++)
        {
            var exitIndex = i;
            await PollAsync(
                () => spawnCount > exitIndex && supervisor.Status == PythonPluginStatus.Ready,
                timeout: TimeSpan.FromSeconds(5),
                reason: $"spawn {exitIndex + 1} should be Ready before exit {exitIndex + 1}");

            handles[exitIndex].SimulateExit();
        }

        // After 3 exits the last restart attempt runs; 4th exit → Unavailable.
        handles[Total - 1].SimulateExit();

        await PollAsync(
            () => supervisor.Status == PythonPluginStatus.Unavailable,
            timeout: TimeSpan.FromSeconds(5),
            reason: "after all restarts exhausted status should be Unavailable");

        await responderCts.CancelAsync();
        await supervisor.StopAsync();
    }

    [Fact]
    public async Task StopAsync_WhileReady_GracefulShutdown()
    {
        var pipes = MakePipes();
        var handle = new FakeSubprocessHandle(pipes.SupervisorInput, pipes.SupervisorOutput);
        var responder = new MockMcpResponder(pipes.ResponderInput, pipes.ResponderOutput);

        using var responderCts = new CancellationTokenSource();
        _ = Task.Run(() => responder.RunAsync(responderCts.Token));

        var supervisor = new PythonSubprocessSupervisor(
            MakeDescriptor(),
            NullLoggerFactory.Instance,
            _ => handle,
            FastBackoff);

        supervisor.Start();
        await supervisor.InitialHandshakeTask;

        supervisor.Status.Should().Be(PythonPluginStatus.Ready);

        // StopAsync should complete without hanging.
        var stopTask = supervisor.StopAsync();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(10));

        await responderCts.CancelAsync();
    }

    [Fact]
    public async Task StopAsync_BeforeHandshakeCompletes_CompletesCleanly()
    {
        var pipes = MakePipes();
        var handle = new FakeSubprocessHandle(pipes.SupervisorInput, pipes.SupervisorOutput);
        // No responder — handshake would time out but we stop first.

        var supervisor = new PythonSubprocessSupervisor(
            MakeDescriptor(handshakeTimeoutSeconds: 30),
            NullLoggerFactory.Instance,
            _ => handle,
            FastBackoff);

        supervisor.Start();

        // Stop before handshake completes.
        var stopTask = supervisor.StopAsync();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Handshake TCS should be resolved (false) after stop.
        supervisor.InitialHandshakeTask.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task DeclaredToolNotInServerList_WarnsButStaysReady()
    {
        var pipes = MakePipes();
        var handle = new FakeSubprocessHandle(pipes.SupervisorInput, pipes.SupervisorOutput);
        // Server returns "tool_a" only; descriptor also declares "tool_b" (not returned).
        var responder = new MockMcpResponder(pipes.ResponderInput, pipes.ResponderOutput, ["tool_a"]);

        using var responderCts = new CancellationTokenSource();
        _ = Task.Run(() => responder.RunAsync(responderCts.Token));

        var supervisor = new PythonSubprocessSupervisor(
            MakeDescriptor(declaredTools: ["tool_a", "tool_b"]),
            NullLoggerFactory.Instance,
            _ => handle,
            FastBackoff);

        supervisor.Start();
        var ok = await supervisor.InitialHandshakeTask;

        // Mismatch is a WARN, not a failure — plugin stays Ready.
        ok.Should().BeTrue();
        supervisor.Status.Should().Be(PythonPluginStatus.Ready);

        await responderCts.CancelAsync();
        await supervisor.StopAsync();
    }
}
