// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Vais.Agents.Core;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Python.Tests;

/// <summary>
/// Part 2 (run-health) RH-8 / RH-9 — verifies that <see cref="PythonAgentShim"/> spans carry
/// the correct Langfuse observation level tags and that child spans nest under an ambient parent.
/// </summary>
public sealed class PythonAgentShimObservabilityTests
{
    // ── Fake channel ──────────────────────────────────────────────────────────

    private sealed class FakeChannel : IPythonAgentChannel
    {
        private readonly Func<AgentInvokeRequest, AgentInvokeResponse> _handler;
        internal FakeChannel(Func<AgentInvokeRequest, AgentInvokeResponse>? handler = null)
        {
            _handler = handler ?? (req => new AgentInvokeResponse($"Echo: {req.UserMessage}", null, null, null));
            Descriptor = new PythonPluginDescriptor(
                Name: "obs-test", PluginDirectory: "/fake", InterpreterPath: "/py",
                EntrypointPath: "/srv.py", TargetApiVersion: "0.23",
                HandshakeTimeoutSeconds: 5, RestartPolicy: PythonRestartPolicy.Never,
                DeclaredTools: [], SecretRefs: new Dictionary<string, string>(), HandlerKind: PythonHandlerKind.AgentHandler,
                HandlerTypeName: "Fake", InvokeTimeoutSeconds: 30);
        }
        public PythonPluginDescriptor Descriptor { get; }
        public Task<AgentInvokeResponse> InvokeAgentAsync(AgentInvokeRequest req, CancellationToken ct)
            => Task.FromResult(_handler(req));

        public async IAsyncEnumerable<AgentStreamFrame> StreamAgentAsync(
            AgentInvokeRequest req, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            var resp = _handler(req);
            yield return new AgentStreamFrame(resp.AssistantMessage, null);
            yield return new AgentStreamFrame(null, resp);
        }
    }

    private sealed class ThrowingChannel : IPythonAgentChannel
    {
        public PythonPluginDescriptor Descriptor { get; } = new(
            Name: "obs-test", PluginDirectory: "/fake", InterpreterPath: "/py",
            EntrypointPath: "/srv.py", TargetApiVersion: "0.23",
            HandshakeTimeoutSeconds: 5, RestartPolicy: PythonRestartPolicy.Never,
            DeclaredTools: [], SecretRefs: new Dictionary<string, string>());
        public Task<AgentInvokeResponse> InvokeAgentAsync(AgentInvokeRequest req, CancellationToken ct)
            => Task.FromException<AgentInvokeResponse>(new InvalidOperationException("plugin-down"));
        public async IAsyncEnumerable<AgentStreamFrame> StreamAgentAsync(
            AgentInvokeRequest req, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.FromException(new InvalidOperationException("stream-down"));
            yield break;
        }
    }

    private static PythonAgentShim MakeShim(
        Func<AgentInvokeRequest, AgentInvokeResponse>? handler = null,
        string agentId = "obs-agent")
    {
        var ch = handler is not null ? new FakeChannel(handler) : (IPythonAgentChannel)new FakeChannel();
        return new PythonAgentShim(ch, new InMemoryAgentSession(agentId), maxStateSizeBytes: 0);
    }

    private static PythonAgentShim MakeThrowingShim(string agentId = "obs-agent")
        => new(new ThrowingChannel(), new InMemoryAgentSession(agentId), maxStateSizeBytes: 0);

    private static async Task<List<AgentEvent>> CollectStreamAsync(PythonAgentShim shim, string message = "hello")
    {
        var events = new List<AgentEvent>();
        await foreach (var e in shim.StreamAsync(message, AgentContext.Empty, CancellationToken.None))
            events.Add(e);
        return events;
    }

    // ── Helpers for capturing Activity data ──────────────────────────────────

    private static IDisposable ListenToSource(
        string sourceName,
        List<Activity> captured)
    {
        return new ActivityListener
        {
            ShouldListenTo = s => s.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => captured.Add(a),
        }.Tap(l => ActivitySource.AddActivityListener(l));
    }

    // ── RH-8: AskAsync error sets span status ────────────────────────────────

    [Fact]
    public async Task AskAsync_ChannelThrows_SetsSpanStatusError()
    {
        var agentId = $"err-agent-{Guid.NewGuid():N}";
        var activities = new List<Activity>();
        using var _ = ListenToSource("Vais.Agents.Runtime.Plugins.Python", activities);

        var shim = MakeThrowingShim(agentId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => shim.AskAsync("test"));

        var ask = activities
            .Where(a => a.OperationName == "python.agent.ask" &&
                        a.GetTagItem("vais.agent.name") as string == agentId)
            .Should().ContainSingle().Subject;
        ask.Status.Should().Be(ActivityStatusCode.Error);
        ask.GetTagItem("langfuse.observation.status_message").Should().NotBeNull();
    }

    // ── RH-8: AskAsync partial sets WARNING level tag ────────────────────────

    [Fact]
    public async Task AskAsync_PartialResponse_SetsWarningLevelTag()
    {
        var agentId = $"partial-agent-{Guid.NewGuid():N}";
        var activities = new List<Activity>();
        using var _ = ListenToSource("Vais.Agents.Runtime.Plugins.Python", activities);

        var shim = MakeShim(_ => new AgentInvokeResponse(
            "partial", null, null, null, IsPartial: true, FailureReason: "maxItems exceeded"), agentId);

        await shim.AskAsync("test");

        var ask = activities
            .Where(a => a.OperationName == "python.agent.ask" &&
                        a.GetTagItem("vais.agent.name") as string == agentId)
            .Should().ContainSingle().Subject;
        ask.GetTagItem("langfuse.observation.level").Should().Be("WARNING");
        ask.GetTagItem("vais.turn.partial").Should().Be(true);
        ask.GetTagItem("langfuse.observation.status_message").Should().Be("maxItems exceeded");
    }

    // ── RH-9: span nesting — child span inherits parent trace id ────────────

    [Fact]
    public async Task AskAsync_WithAmbientParent_NestsSpanUnderParent()
    {
        var agentId = $"nest-agent-{Guid.NewGuid():N}";
        var activities = new List<Activity>();
        using var _ = ListenToSource("Vais.Agents.Runtime.Plugins.Python", activities);

        // Simulate a parent span (e.g. graph.node) being active.
        using var parentSource = new ActivitySource("test.parent");
        using var parentListener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "test.parent",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(parentListener);

        using var parent = parentSource.StartActivity("parent.span");
        parent.Should().NotBeNull("listener must be attached before StartActivity");
        var parentTraceId = parent!.TraceId;

        var shim = MakeShim(agentId: agentId);
        await shim.AskAsync("hello");

        var ask = activities
            .Where(a => a.OperationName == "python.agent.ask" &&
                        a.GetTagItem("vais.agent.name") as string == agentId)
            .Should().ContainSingle().Subject;
        ask.TraceId.Should().Be(parentTraceId, "child span must share the parent trace id");
        ask.ParentId.Should().Be(parent.Id, "child span must be parented to the ambient span");
    }

    // ── RH-9: python.agent.stream span exists and nests ──────────────────────

    [Fact]
    public async Task StreamAsync_WithAmbientParent_CreatesStreamSpanUnderParent()
    {
        var agentId = $"nest-stream-{Guid.NewGuid():N}";
        var activities = new List<Activity>();
        using var _ = ListenToSource("Vais.Agents.Runtime.Plugins.Python", activities);

        using var parentSource = new ActivitySource("test.parent2");
        using var parentListener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "test.parent2",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(parentListener);

        using var parent = parentSource.StartActivity("parent.span2");
        var parentTraceId = parent!.TraceId;

        var shim = MakeShim(agentId: agentId);
        await CollectStreamAsync(shim);

        var stream = activities
            .Where(a => a.OperationName == "python.agent.stream" &&
                        a.GetTagItem("vais.agent.name") as string == agentId)
            .Should().ContainSingle().Subject;
        stream.TraceId.Should().Be(parentTraceId);
        stream.ParentId.Should().Be(parent.Id);
    }

    // ── RH-8: StreamAsync error sets span status ─────────────────────────────

    [Fact]
    public async Task StreamAsync_ChannelThrows_SetsSpanStatusError()
    {
        var agentId = $"throw-agent-{Guid.NewGuid():N}";
        var activities = new List<Activity>();
        using var _ = ListenToSource("Vais.Agents.Runtime.Plugins.Python", activities);

        var shim = MakeThrowingShim(agentId);
        var events = await CollectStreamAsync(shim);

        events.Should().Contain(e => e is TurnFailed);

        // Filter by agent name tag to avoid cross-test contamination in shared ActivitySource.
        var stream = activities
            .Where(a => a.OperationName == "python.agent.stream" &&
                        a.GetTagItem("vais.agent.name") as string == agentId)
            .Should().ContainSingle().Subject;
        stream.Status.Should().Be(ActivityStatusCode.Error);
        stream.GetTagItem("langfuse.observation.status_message").Should().NotBeNull();
    }

    // ── RH-8: StreamAsync partial sets WARNING level ──────────────────────────

    [Fact]
    public async Task StreamAsync_PartialResponse_SetsWarningLevelAndTurnCompletedWarning()
    {
        var agentId = $"partial-stream-{Guid.NewGuid():N}";
        var activities = new List<Activity>();
        using var _ = ListenToSource("Vais.Agents.Runtime.Plugins.Python", activities);

        var shim = MakeShim(_ => new AgentInvokeResponse(
            "partial text", null, null, null, IsPartial: true, FailureReason: "upstream cap"), agentId);

        var events = await CollectStreamAsync(shim);

        var completed = events.OfType<TurnCompleted>().Should().ContainSingle().Subject;
        completed.Level.Should().Be(FailureLevel.Warning);

        var stream = activities
            .Where(a => a.OperationName == "python.agent.stream" &&
                        a.GetTagItem("vais.agent.name") as string == agentId)
            .Should().ContainSingle().Subject;
        stream.GetTagItem("langfuse.observation.level").Should().Be("WARNING");
        stream.GetTagItem("vais.turn.partial").Should().Be(true);
    }
}

internal static class ActivityListenerExtensions
{
    internal static T Tap<T>(this T value, Action<T> action) { action(value); return value; }
}
