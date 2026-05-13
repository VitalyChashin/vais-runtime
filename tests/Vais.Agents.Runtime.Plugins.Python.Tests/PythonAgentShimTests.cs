// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using FluentAssertions;
using Vais.Agents.Core;
using Xunit;
using IOpaqueStateCarrier = Vais.Agents.Core.IOpaqueStateCarrier;

namespace Vais.Agents.Runtime.Plugins.Python.Tests;

/// <summary>
/// Unit tests for <see cref="PythonAgentShim"/>. Uses a fake <see cref="IPythonAgentChannel"/>
/// to verify shim logic without a real subprocess or MCP client.
/// </summary>
public sealed class PythonAgentShimTests
{
    // -------------------------------------------------------------------------
    // Fake channel
    // -------------------------------------------------------------------------

    private sealed class FakeChannel : IPythonAgentChannel
    {
        private readonly Func<AgentInvokeRequest, AgentInvokeResponse> _handler;

        internal FakeChannel(
            Func<AgentInvokeRequest, AgentInvokeResponse>? handler = null,
            string handlerTypeName = "Acme.MyAgent",
            int invokeTimeoutSeconds = 60)
        {
            _handler = handler ?? (req => new AgentInvokeResponse(
                $"Echo: {req.UserMessage}", null, null, null));
            Descriptor = new PythonPluginDescriptor(
                Name: "fake-plugin",
                PluginDirectory: "/fake",
                InterpreterPath: "/fake/python",
                EntrypointPath: "/fake/server.py",
                TargetApiVersion: "0.23",
                HandshakeTimeoutSeconds: 5,
                RestartPolicy: PythonRestartPolicy.Never,
                DeclaredTools: [],
                SecretRefs: new Dictionary<string, string>(),
                HandlerKind: PythonHandlerKind.AgentHandler,
                HandlerTypeName: handlerTypeName,
                InvokeTimeoutSeconds: invokeTimeoutSeconds);
        }

        public PythonPluginDescriptor Descriptor { get; }

        public Task<AgentInvokeResponse> InvokeAgentAsync(AgentInvokeRequest request, CancellationToken ct)
            => Task.FromResult(_handler(request));

        public async IAsyncEnumerable<AgentStreamFrame> StreamAgentAsync(
            AgentInvokeRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            var resp = _handler(request);
            yield return new AgentStreamFrame(resp.AssistantMessage, null);
            yield return new AgentStreamFrame(null, resp);
        }
    }

    private sealed class ThrowingChannel : IPythonAgentChannel
    {
        private readonly Exception _exception;

        internal ThrowingChannel(Exception? exception = null)
        {
            _exception = exception ?? new InvalidOperationException(
                $"[{PythonPluginUrns.Unavailable}] Python plugin 'fake' is unavailable.");
            Descriptor = new PythonPluginDescriptor(
                Name: "fake-plugin",
                PluginDirectory: "/fake",
                InterpreterPath: "/fake/python",
                EntrypointPath: "/fake/server.py",
                TargetApiVersion: "0.23",
                HandshakeTimeoutSeconds: 5,
                RestartPolicy: PythonRestartPolicy.Never,
                DeclaredTools: [],
                SecretRefs: new Dictionary<string, string>());
        }

        public PythonPluginDescriptor Descriptor { get; }

        public Task<AgentInvokeResponse> InvokeAgentAsync(AgentInvokeRequest request, CancellationToken ct)
            => Task.FromException<AgentInvokeResponse>(_exception);

        public async IAsyncEnumerable<AgentStreamFrame> StreamAgentAsync(
            AgentInvokeRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.FromException(_exception);
            yield break;
        }
    }

    private static PythonAgentShim MakeShim(
        Func<AgentInvokeRequest, AgentInvokeResponse>? handler = null,
        int maxStateSizeBytes = 0,
        string agentId = "agent-1")
    {
        var channel = new FakeChannel(handler);
        var session = new InMemoryAgentSession(agentId);
        return new PythonAgentShim(channel, session, maxStateSizeBytes);
    }

    // -------------------------------------------------------------------------
    // AskAsync — basic path
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AskAsync_ValidResponse_ReturnsAssistantMessage()
    {
        var shim = MakeShim(_ => new AgentInvokeResponse("Hello from Python!", null, null, null));

        var reply = await shim.AskAsync("Hi there");

        reply.Should().Be("Hello from Python!");
    }

    [Fact]
    public async Task AskAsync_ValidResponse_AppendsBothTurnsToHistory()
    {
        var shim = MakeShim(_ => new AgentInvokeResponse("Assistant reply", null, null, null));

        await shim.AskAsync("User message");

        shim.History.Should().HaveCount(2);
        shim.History[0].Role.Should().Be(AgentChatRole.User);
        shim.History[0].Text.Should().Be("User message");
        shim.History[1].Role.Should().Be(AgentChatRole.Assistant);
        shim.History[1].Text.Should().Be("Assistant reply");
    }

    [Fact]
    public async Task AskAsync_MultipleTurns_AccumulatesHistory()
    {
        var shim = MakeShim(req => new AgentInvokeResponse($"Reply to: {req.UserMessage}", null, null, null));

        await shim.AskAsync("one");
        await shim.AskAsync("two");

        shim.History.Should().HaveCount(4);
    }

    // -------------------------------------------------------------------------
    // AskAsync — state round-trip
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AskAsync_StateRoundTrip_SendsNullStateOnFirstCall()
    {
        string? receivedState = "sentinel";
        var shim = MakeShim(req =>
        {
            receivedState = req.State;
            return new AgentInvokeResponse("reply", "state-1", null, null);
        });

        await shim.AskAsync("turn 1");

        receivedState.Should().BeNull("first call has no prior state");
    }

    [Fact]
    public async Task AskAsync_StateRoundTrip_SendsPreviousStateOnSubsequentCall()
    {
        var receivedStates = new List<string?>();
        var shim = MakeShim(req =>
        {
            receivedStates.Add(req.State);
            return new AgentInvokeResponse("reply", $"state-{receivedStates.Count}", null, null);
        });

        await shim.AskAsync("turn 1");
        await shim.AskAsync("turn 2");

        receivedStates.Should().HaveCount(2);
        receivedStates[0].Should().BeNull();
        receivedStates[1].Should().Be("state-1");
    }

    [Fact]
    public async Task AskAsync_ChannelReturnsNullNewState_PreservesNullState()
    {
        var receivedStates = new List<string?>();
        var shim = MakeShim(req =>
        {
            receivedStates.Add(req.State);
            return new AgentInvokeResponse("reply", NewState: null, null, null);
        });

        await shim.AskAsync("turn 1");
        await shim.AskAsync("turn 2");

        receivedStates[1].Should().BeNull("null newState means: keep null state");
    }

    // -------------------------------------------------------------------------
    // AskAsync — request fields
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AskAsync_SendsCorrectAgentIdAndSessionId()
    {
        AgentInvokeRequest? captured = null;
        var channel = new FakeChannel(req =>
        {
            captured = req;
            return new AgentInvokeResponse("ok", null, null, null);
        });
        var session = new InMemoryAgentSession("my-agent");
        var shim = new PythonAgentShim(channel, session, maxStateSizeBytes: 0);

        await shim.AskAsync("hello");

        captured!.AgentId.Should().Be("my-agent");
        captured.SessionId.Should().Be(session.SessionId);
        captured.UserMessage.Should().Be("hello");
    }

    // -------------------------------------------------------------------------
    // AskAsync — error handling
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AskAsync_ChannelThrowsUnavailable_PropagatesException()
    {
        var channel = new ThrowingChannel(
            new InvalidOperationException($"[{PythonPluginUrns.Unavailable}] unavailable"));
        var session = new InMemoryAgentSession("agent");
        var shim = new PythonAgentShim(channel, session, maxStateSizeBytes: 0);

        var act = () => shim.AskAsync("hello");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{PythonPluginUrns.Unavailable}*");
    }

    [Fact]
    public async Task AskAsync_StateTooLarge_Throws()
    {
        const int maxBytes = 10;
        var shim = MakeShim(
            _ => new AgentInvokeResponse("reply", new string('X', 100), null, null),
            maxStateSizeBytes: maxBytes);

        var act = () => shim.AskAsync("hello");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{PythonPluginUrns.AgentStateTooLarge}*");
    }

    [Fact]
    public async Task AskAsync_StateSizeDisabled_LargeStateAccepted()
    {
        // maxStateSizeBytes = 0 means disabled — any state size is OK.
        var shim = MakeShim(
            _ => new AgentInvokeResponse("reply", new string('X', 1_000_000), null, null),
            maxStateSizeBytes: 0);

        var reply = await shim.AskAsync("hello");
        reply.Should().Be("reply");
    }

    // -------------------------------------------------------------------------
    // Reset
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Reset_AfterTurns_ClearsHistory()
    {
        var shim = MakeShim(_ => new AgentInvokeResponse("reply", "state", null, null));

        await shim.AskAsync("hi");
        shim.History.Should().HaveCount(2);

        shim.Reset();

        shim.History.Should().BeEmpty();
    }

    [Fact]
    public async Task Reset_ClearsOpaqueState_NextCallSendsNullState()
    {
        var receivedStates = new List<string?>();
        var shim = MakeShim(req =>
        {
            receivedStates.Add(req.State);
            return new AgentInvokeResponse("reply", "non-null-state", null, null);
        });

        await shim.AskAsync("before reset");
        shim.Reset();
        await shim.AskAsync("after reset");

        receivedStates.Should().HaveCount(2);
        receivedStates[1].Should().BeNull("reset clears the opaque state");
    }

    // -------------------------------------------------------------------------
    // Properties
    // -------------------------------------------------------------------------

    [Fact]
    public void Session_ReturnsTheSameSessionPassedAtConstruction()
    {
        var channel = new FakeChannel();
        var session = new InMemoryAgentSession("my-agent");
        var shim = new PythonAgentShim(channel, session, maxStateSizeBytes: 0);

        shim.Session.Should().BeSameAs(session);
    }

    [Fact]
    public void SystemPrompt_CanBeSetAndRead()
    {
        var shim = MakeShim();

        shim.SystemPrompt.Should().BeNull();
        shim.SystemPrompt = "You are a helpful assistant.";
        shim.SystemPrompt.Should().Be("You are a helpful assistant.");
    }

    [Fact]
    public void History_IsAliasForSessionHistory()
    {
        var channel = new FakeChannel();
        var session = new InMemoryAgentSession("my-agent");
        var shim = new PythonAgentShim(channel, session, maxStateSizeBytes: 0);

        shim.History.Should().BeSameAs(session.History);
    }

    // -------------------------------------------------------------------------
    // IOpaqueStateCarrier
    // -------------------------------------------------------------------------

    [Fact]
    public async Task IOpaqueStateCarrier_AfterAsk_ExposesNewState()
    {
        var shim = MakeShim(_ => new AgentInvokeResponse("reply", "persisted-blob", null, null));

        await shim.AskAsync("hi");

        ((IOpaqueStateCarrier)shim).OpaqueState.Should().Be("persisted-blob");
    }

    [Fact]
    public async Task IOpaqueStateCarrier_SetBeforeAsk_SentAsStateOnNextCall()
    {
        string? receivedState = null;
        var shim = MakeShim(req =>
        {
            receivedState = req.State;
            return new AgentInvokeResponse("reply", "new-blob", null, null);
        });

        ((IOpaqueStateCarrier)shim).OpaqueState = "injected-blob";
        await shim.AskAsync("hi");

        receivedState.Should().Be("injected-blob");
    }

    [Fact]
    public async Task IOpaqueStateCarrier_AfterReset_ExposesNull()
    {
        var shim = MakeShim(_ => new AgentInvokeResponse("reply", "some-blob", null, null));

        await shim.AskAsync("hi");
        shim.Reset();

        ((IOpaqueStateCarrier)shim).OpaqueState.Should().BeNull();
    }
}
