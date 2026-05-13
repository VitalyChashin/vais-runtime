// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.IO.Pipelines;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Vais.Agents.Core;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Python.Tests;

/// <summary>
/// Tests for <c>vais/agent.stream</c>: supervisor <see cref="PythonSubprocessSupervisor.StreamAgentAsync"/>
/// and shim <see cref="PythonAgentShim.StreamAsync"/>.
/// Uses in-memory pipes + <see cref="AgentMockMcpResponder"/> — no real subprocess.
/// </summary>
public sealed class PythonAgentStreamingTests
{
    private static readonly TimeSpan[] FastBackoff =
        [TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20)];

    // -------------------------------------------------------------------------
    // Pipe/handle helpers (mirrors PythonSubprocessSupervisorTests)
    // -------------------------------------------------------------------------

    private static (PipeSetup Pipes, AgentMockMcpResponder Responder, FakeSubprocessHandle Handle)
        MakeAgent(
            Func<AgentInvokeRequest, AgentInvokeResponse>? invokeHandler = null,
            Func<string, AgentInvokeRequest, (IEnumerable<string> Chunks, AgentInvokeResponse Response)>?
                streamHandler = null,
            int pid = 42)
    {
        var c2s = new Pipe();
        var s2c = new Pipe();
        var pipes = new PipeSetup(
            c2s.Writer.AsStream(), s2c.Reader.AsStream(),
            c2s.Reader.AsStream(), s2c.Writer.AsStream());
        var handle = new FakeSubprocessHandle(pipes.SupervisorInput, pipes.SupervisorOutput, pid);
        var responder = new AgentMockMcpResponder(
            pipes.ResponderInput, pipes.ResponderOutput,
            invokeHandler, streamHandler);
        return (pipes, responder, handle);
    }

    private sealed record PipeSetup(
        Stream SupervisorInput, Stream SupervisorOutput,
        Stream ResponderInput, Stream ResponderOutput);

    private static PythonPluginDescriptor MakeDescriptor(string name = "test-plugin") =>
        new PythonPluginDescriptor(
            Name: name,
            PluginDirectory: "/fake",
            InterpreterPath: "/fake/python",
            EntrypointPath: "/fake/server.py",
            TargetApiVersion: "0.26",
            HandshakeTimeoutSeconds: 5,
            RestartPolicy: PythonRestartPolicy.Never,
            DeclaredTools: [],
            SecretRefs: new Dictionary<string, string>(),
            HandlerKind: PythonHandlerKind.AgentHandler,
            HandlerTypeName: "TestAgent",
            InvokeTimeoutSeconds: 30);

    // -------------------------------------------------------------------------
    // Supervisor streaming tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task StreamAgentAsync_ThreeChunks_YieldsDeltaFramesThenTerminal()
    {
        var chunks = new[] { "Hello", ", ", "world!" };
        var (_, responder, handle) = MakeAgent(
            streamHandler: (_, req) => (
                chunks,
                new AgentInvokeResponse("Hello, world!", req.State, null, null)));

        using var responderCts = new CancellationTokenSource();
        _ = Task.Run(() => responder.RunAsync(responderCts.Token));

        var supervisor = new PythonSubprocessSupervisor(
            MakeDescriptor(), NullLoggerFactory.Instance,
            _ => handle, FastBackoff);
        supervisor.Start();
        (await supervisor.InitialHandshakeTask).Should().BeTrue();

        var request = new AgentInvokeRequest(
            "test-agent", "sess-1", "Hello", null, 30, null);

        var frames = new List<AgentStreamFrame>();
        await foreach (var frame in supervisor.StreamAgentAsync(request, CancellationToken.None))
            frames.Add(frame);

        var deltaFrames = frames.Where(f => f.TextDelta is not null).ToList();
        var terminalFrames = frames.Where(f => f.FinalResponse is not null).ToList();

        deltaFrames.Should().HaveCount(3);
        deltaFrames.Select(f => f.TextDelta).Should().Equal("Hello", ", ", "world!");
        terminalFrames.Should().ContainSingle();
        terminalFrames[0].FinalResponse!.AssistantMessage.Should().Be("Hello, world!");

        await responderCts.CancelAsync();
        await supervisor.StopAsync();
    }

    [Fact]
    public async Task StreamAgentAsync_NoStreamHandler_FallsBackToInvoke()
    {
        var (_, responder, handle) = MakeAgent(
            invokeHandler: req => new AgentInvokeResponse($"Echo: {req.UserMessage}", null, null, null));

        using var responderCts = new CancellationTokenSource();
        _ = Task.Run(() => responder.RunAsync(responderCts.Token));

        var supervisor = new PythonSubprocessSupervisor(
            MakeDescriptor(), NullLoggerFactory.Instance,
            _ => handle, FastBackoff);
        supervisor.Start();
        (await supervisor.InitialHandshakeTask).Should().BeTrue();

        var request = new AgentInvokeRequest("a", "s", "Hi", null, 30, null);
        var frames = new List<AgentStreamFrame>();
        await foreach (var frame in supervisor.StreamAgentAsync(request, CancellationToken.None))
            frames.Add(frame);

        frames.Should().HaveCount(2, "one delta + one terminal");
        frames[0].TextDelta.Should().Be("Echo: Hi");
        frames[1].FinalResponse.Should().NotBeNull();

        await responderCts.CancelAsync();
        await supervisor.StopAsync();
    }

    [Fact]
    public async Task StreamAgentAsync_WhileNotReady_Throws()
    {
        var (_, responder, handle) = MakeAgent();
        using var responderCts = new CancellationTokenSource();
        _ = Task.Run(() => responder.RunAsync(responderCts.Token));

        var supervisor = new PythonSubprocessSupervisor(
            MakeDescriptor(), NullLoggerFactory.Instance,
            _ => handle, FastBackoff);
        // Don't call Start() — supervisor stays in Loading state.

        var act = async () =>
        {
            await foreach (var _ in supervisor.StreamAgentAsync(
                new AgentInvokeRequest("a", "s", "Hi", null, 30, null), CancellationToken.None))
            {
                // should throw before yielding
            }
        };
        await act.Should().ThrowAsync<InvalidOperationException>();

        await responderCts.CancelAsync();
    }

    // -------------------------------------------------------------------------
    // Shim streaming tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ShimStreamAsync_ProducesCorrectEventSequence()
    {
        var chunks = new[] { "A", "B", "C" };
        var (_, responder, handle) = MakeAgent(
            streamHandler: (_, req) => (
                chunks,
                new AgentInvokeResponse("ABC", req.State, null, null)));

        using var responderCts = new CancellationTokenSource();
        _ = Task.Run(() => responder.RunAsync(responderCts.Token));

        var supervisor = new PythonSubprocessSupervisor(
            MakeDescriptor(), NullLoggerFactory.Instance,
            _ => handle, FastBackoff);
        supervisor.Start();
        (await supervisor.InitialHandshakeTask).Should().BeTrue();

        var session = new InMemoryAgentSession("agent-1", "sess-1");
        var shim = new PythonAgentShim(supervisor, session, maxStateSizeBytes: 0);
        var ctx = new AgentContext(AgentName: "test");

        var events = new List<AgentEvent>();
        await foreach (var evt in shim.StreamAsync("Hello", ctx, CancellationToken.None))
            events.Add(evt);

        events.Should().HaveCount(5, "TurnStarted + 3×CompletionDelta + TurnCompleted");
        events[0].Should().BeOfType<TurnStarted>()
            .Which.UserMessage.Should().Be("Hello");
        events[1].Should().BeOfType<CompletionDelta>().Which.TextDelta.Should().Be("A");
        events[2].Should().BeOfType<CompletionDelta>().Which.TextDelta.Should().Be("B");
        events[3].Should().BeOfType<CompletionDelta>().Which.TextDelta.Should().Be("C");
        events[4].Should().BeOfType<TurnCompleted>()
            .Which.AssistantText.Should().Be("ABC");

        session.History.Should().HaveCount(2, "user + assistant turns appended");

        await responderCts.CancelAsync();
        await supervisor.StopAsync();
    }

    [Fact]
    public async Task ShimStreamAsync_CancelledMidStream_EndsCleanly()
    {
        // Responder that blocks after first chunk — simulates slow stream.
        // We cancel the CT after receiving the first event.
        var gateTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chunks = new[] { "first", "second" }; // second never sent while gate is open
        var (_, responder, handle) = MakeAgent(
            streamHandler: (_, req) => (chunks, new AgentInvokeResponse("first", null, null, null)));

        using var responderCts = new CancellationTokenSource();
        _ = Task.Run(() => responder.RunAsync(responderCts.Token));

        var supervisor = new PythonSubprocessSupervisor(
            MakeDescriptor(), NullLoggerFactory.Instance,
            _ => handle, FastBackoff);
        supervisor.Start();
        (await supervisor.InitialHandshakeTask).Should().BeTrue();

        var session = new InMemoryAgentSession("a", "s");
        var shim = new PythonAgentShim(supervisor, session, maxStateSizeBytes: 0);

        using var streamCts = new CancellationTokenSource();

        var events = new List<AgentEvent>();
        var act = async () =>
        {
            await foreach (var evt in shim.StreamAsync("msg", AgentContext.Empty, streamCts.Token))
            {
                events.Add(evt);
                // Cancel after TurnStarted arrives.
                if (evt is TurnStarted)
                    await streamCts.CancelAsync();
            }
        };

        // Should complete without throwing (cancellation ends cleanly).
        await act.Should().NotThrowAsync();
        events.Should().Contain(e => e is TurnStarted);
        events.Should().NotContain(e => e is TurnFailed);

        await responderCts.CancelAsync();
        await supervisor.StopAsync();
    }
}
