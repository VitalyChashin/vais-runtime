// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.IO.Pipelines;
using System.Reflection;
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
        IReadOnlyList<string>? declaredTools = null,
        string? handlerTypeName = null) =>
        new(
            Name: name,
            PluginDirectory: "/fake",
            InterpreterPath: "/fake/python",
            EntrypointPath: "/fake/server.py",
            TargetApiVersion: "0.23",
            HandshakeTimeoutSeconds: handshakeTimeoutSeconds,
            RestartPolicy: restartPolicy,
            DeclaredTools: declaredTools ?? [],
            SecretRefs: new Dictionary<string, string>(),
            HandlerKind: handlerTypeName is null
                ? PythonHandlerKind.McpToolServer
                : PythonHandlerKind.AgentHandler,
            HandlerTypeName: handlerTypeName);

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

    // -------------------------------------------------------------------------
    // DrainAndRestartAsync tests (v0.25)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DrainAndRestart_NoInFlightInvokes_RestartSucceeds()
    {
        var pipes1 = MakePipes();
        var pipes2 = MakePipes();
        var handle1 = new FakeSubprocessHandle(pipes1.SupervisorInput, pipes1.SupervisorOutput, pid: 10);
        var handle2 = new FakeSubprocessHandle(pipes2.SupervisorInput, pipes2.SupervisorOutput, pid: 20);

        var callCount = 0;
        using var responderCts = new CancellationTokenSource();
        var r1 = new AgentMockMcpResponder(pipes1.ResponderInput, pipes1.ResponderOutput);
        var r2 = new AgentMockMcpResponder(pipes2.ResponderInput, pipes2.ResponderOutput);
        _ = Task.Run(() => r1.RunAsync(responderCts.Token));
        _ = Task.Run(() => r2.RunAsync(responderCts.Token));

        var supervisor = new PythonSubprocessSupervisor(
            MakeDescriptor(handlerTypeName: "MyAgent"),
            NullLoggerFactory.Instance,
            _ => ++callCount == 1 ? handle1 : handle2,
            FastBackoff);

        supervisor.Start();
        (await supervisor.InitialHandshakeTask).Should().BeTrue();
        supervisor.ProcessId.Should().Be(10);

        var newDescriptor = MakeDescriptor(handlerTypeName: "MyAgent");
        var ok = await supervisor.DrainAndRestartAsync(newDescriptor, TimeSpan.FromSeconds(5), default);

        ok.Should().BeTrue();
        supervisor.Status.Should().Be(PythonPluginStatus.Ready);
        supervisor.ProcessId.Should().Be(20);
        callCount.Should().Be(2, because: "second subprocess should have been spawned");

        await responderCts.CancelAsync();
        await supervisor.StopAsync();
    }

    [Fact]
    public async Task DrainAndRestart_WithInFlightInvoke_WaitsForCompletion()
    {
        var pipes1 = MakePipes();
        var pipes2 = MakePipes();
        var handle1 = new FakeSubprocessHandle(pipes1.SupervisorInput, pipes1.SupervisorOutput, pid: 10);
        var handle2 = new FakeSubprocessHandle(pipes2.SupervisorInput, pipes2.SupervisorOutput, pid: 20);

        using var responderCts = new CancellationTokenSource();
        var r1 = new AgentMockMcpResponder(pipes1.ResponderInput, pipes1.ResponderOutput);
        var r2 = new AgentMockMcpResponder(pipes2.ResponderInput, pipes2.ResponderOutput);
        _ = Task.Run(() => r1.RunAsync(responderCts.Token));
        _ = Task.Run(() => r2.RunAsync(responderCts.Token));

        var callCount = 0;
        var supervisor = new PythonSubprocessSupervisor(
            MakeDescriptor(handlerTypeName: "MyAgent"),
            NullLoggerFactory.Instance,
            _ => ++callCount == 1 ? handle1 : handle2,
            FastBackoff);

        supervisor.Start();
        (await supervisor.InitialHandshakeTask).Should().BeTrue();

        // Simulate an in-flight invoke by directly bumping the active-invokes counter.
        // This exercises the drain-wait logic without driving a full MCP roundtrip.
        var supervisorType = typeof(PythonSubprocessSupervisor);
        var activeField = supervisorType.GetField("_activeInvokes", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var drainSignalField = supervisorType.GetField("_drainSignal", BindingFlags.NonPublic | BindingFlags.Instance)!;

        activeField.SetValue(supervisor, 1);

        // Start reload — should wait because _activeInvokes > 0.
        var reloadTask = supervisor.DrainAndRestartAsync(
            MakeDescriptor(handlerTypeName: "MyAgent"),
            TimeSpan.FromSeconds(10),
            default);

        // Give drain time to observe _activeInvokes > 0 and park on drainSignal.Task.
        await Task.Delay(100);
        reloadTask.IsCompleted.Should().BeFalse("reload should be waiting for in-flight invoke");

        // Simulate the invoke completing: fire the drain signal directly.
        // DrainAndRestartAsync resets _activeInvokes itself once it proceeds.
        var drainSignal = (TaskCompletionSource?)drainSignalField.GetValue(supervisor);
        drainSignal!.TrySetResult();

        var ok = await reloadTask.WaitAsync(TimeSpan.FromSeconds(10));
        ok.Should().BeTrue();
        supervisor.ProcessId.Should().Be(20);

        await responderCts.CancelAsync();
        await supervisor.StopAsync();
    }

    [Fact]
    public async Task DrainAndRestart_DrainTimeout_ProceedsAfterTimeout()
    {
        var pipes1 = MakePipes();
        var pipes2 = MakePipes();
        var handle1 = new FakeSubprocessHandle(pipes1.SupervisorInput, pipes1.SupervisorOutput, pid: 10);
        var handle2 = new FakeSubprocessHandle(pipes2.SupervisorInput, pipes2.SupervisorOutput, pid: 20);

        // Responder 1 blocks the invoke forever.
        var invokeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var responderCts = new CancellationTokenSource();
        var r1 = new AgentMockMcpResponder(pipes1.ResponderInput, pipes1.ResponderOutput, req =>
        {
            invokeStarted.TrySetResult();
            Task.Delay(Timeout.Infinite).GetAwaiter().GetResult(); // block forever
            return new AgentInvokeResponse("never", null, null, null);
        });
        var r2 = new AgentMockMcpResponder(pipes2.ResponderInput, pipes2.ResponderOutput);
        _ = Task.Run(() => r1.RunAsync(responderCts.Token));
        _ = Task.Run(() => r2.RunAsync(responderCts.Token));

        var callCount = 0;
        var supervisor = new PythonSubprocessSupervisor(
            MakeDescriptor(handlerTypeName: "MyAgent"),
            NullLoggerFactory.Instance,
            _ => ++callCount == 1 ? handle1 : handle2,
            FastBackoff);

        supervisor.Start();
        (await supervisor.InitialHandshakeTask).Should().BeTrue();

        var invokeRequest = new AgentInvokeRequest("agent1", "session1", "hello", null, 60, null);
        _ = Task.Run(() => supervisor.InvokeAgentAsync(invokeRequest, default));
        await invokeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Reload with a very short drain timeout — should proceed without waiting.
        var ok = await supervisor.DrainAndRestartAsync(
            MakeDescriptor(handlerTypeName: "MyAgent"),
            TimeSpan.FromMilliseconds(100),
            default).WaitAsync(TimeSpan.FromSeconds(10));

        ok.Should().BeTrue("reload should succeed despite stuck invoke");
        supervisor.ProcessId.Should().Be(20);

        await responderCts.CancelAsync();
        await supervisor.StopAsync();
    }

    [Fact]
    public async Task DrainAndRestart_HandlerTypeNameChanged_ReturnsFalse()
    {
        var pipes = MakePipes();
        var handle = new FakeSubprocessHandle(pipes.SupervisorInput, pipes.SupervisorOutput, pid: 10);

        using var responderCts = new CancellationTokenSource();
        var responder = new AgentMockMcpResponder(pipes.ResponderInput, pipes.ResponderOutput);
        _ = Task.Run(() => responder.RunAsync(responderCts.Token));

        var supervisor = new PythonSubprocessSupervisor(
            MakeDescriptor(handlerTypeName: "OldAgent"),
            NullLoggerFactory.Instance,
            _ => handle,
            FastBackoff);

        supervisor.Start();
        (await supervisor.InitialHandshakeTask).Should().BeTrue();

        // Different HandlerTypeName — must be refused.
        var newDescriptor = MakeDescriptor(handlerTypeName: "NewAgent");
        var ok = await supervisor.DrainAndRestartAsync(newDescriptor, TimeSpan.FromSeconds(5), default);

        ok.Should().BeFalse("changing HandlerTypeName requires silo restart");
        // Old subprocess should still be alive.
        supervisor.Status.Should().Be(PythonPluginStatus.Ready);
        supervisor.ProcessId.Should().Be(10);

        await responderCts.CancelAsync();
        await supervisor.StopAsync();
    }

    [Fact]
    public async Task DrainAndRestart_ConcurrentCalls_Serialized()
    {
        // Third pipes for second reload
        var pipes1 = MakePipes();
        var pipes2 = MakePipes();
        var pipes3 = MakePipes();
        var callCount = 0;
        var handles = new[]
        {
            new FakeSubprocessHandle(pipes1.SupervisorInput, pipes1.SupervisorOutput, pid: 10),
            new FakeSubprocessHandle(pipes2.SupervisorInput, pipes2.SupervisorOutput, pid: 20),
            new FakeSubprocessHandle(pipes3.SupervisorInput, pipes3.SupervisorOutput, pid: 30),
        };

        using var responderCts = new CancellationTokenSource();
        foreach (var p in new[] { pipes1, pipes2, pipes3 })
        {
            var r = new AgentMockMcpResponder(p.ResponderInput, p.ResponderOutput);
            _ = Task.Run(() => r.RunAsync(responderCts.Token));
        }

        var supervisor = new PythonSubprocessSupervisor(
            MakeDescriptor(handlerTypeName: "MyAgent"),
            NullLoggerFactory.Instance,
            _ => handles[callCount++],
            FastBackoff);

        supervisor.Start();
        (await supervisor.InitialHandshakeTask).Should().BeTrue();

        var desc = MakeDescriptor(handlerTypeName: "MyAgent");
        var reload1 = supervisor.DrainAndRestartAsync(desc, TimeSpan.FromSeconds(5), default);
        var reload2 = supervisor.DrainAndRestartAsync(desc, TimeSpan.FromSeconds(5), default);

        var r1Ok = await reload1.WaitAsync(TimeSpan.FromSeconds(15));
        var r2Ok = await reload2.WaitAsync(TimeSpan.FromSeconds(15));

        r1Ok.Should().BeTrue();
        r2Ok.Should().BeTrue();
        // Both reloads ran sequentially via _reloadLock: 3 total spawns (initial + 2 reloads).
        callCount.Should().Be(3);
        supervisor.ProcessId.Should().Be(30);

        await responderCts.CancelAsync();
        await supervisor.StopAsync();
    }
}
