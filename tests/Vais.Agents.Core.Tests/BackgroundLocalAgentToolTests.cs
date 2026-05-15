// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Vais.Agents.Hosting.InMemory;
using Xunit;

namespace Vais.Agents.Core.Tests;

public sealed class BackgroundLocalAgentToolTests
{
    // ── Schema tests ────────────────────────────────────────────────────────

    [Fact]
    public void Default_Schema_Has_Only_Message_Property()
    {
        var tool = MakeTool(allowCallerSuppliedSession: false);

        var schema = tool.ParametersSchema;
        var props = schema.GetProperty("properties");
        props.TryGetProperty("message", out _).Should().BeTrue();
        props.TryGetProperty("sessionId", out _).Should().BeFalse();

        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        required.Should().ContainSingle("message");
    }

    [Fact]
    public void Session_Schema_Has_Message_And_Optional_SessionId()
    {
        var tool = MakeTool(allowCallerSuppliedSession: true);

        var schema = tool.ParametersSchema;
        var props = schema.GetProperty("properties");
        props.TryGetProperty("message", out _).Should().BeTrue();
        props.TryGetProperty("sessionId", out _).Should().BeTrue();

        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        required.Should().ContainSingle("message");
        required.Should().NotContain("sessionId");
    }

    // ── Depth guard ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Returns_Error_When_MaxChainDepth_Is_Zero()
    {
        var tool = MakeTool();

        using var _ = new AsyncLocalAgentContextAccessor().Push(new AgentContext { MaxChainDepth = 0 });

        var result = await tool.InvokeAsync(MakeArgs("hello"), CancellationToken.None);

        result.Should().Contain("Chain depth limit reached");
    }

    [Fact]
    public async Task Returns_Error_When_MaxChainDepth_Is_Negative()
    {
        var tool = MakeTool();

        using var _ = new AsyncLocalAgentContextAccessor().Push(new AgentContext { MaxChainDepth = -1 });

        var result = await tool.InvokeAsync(MakeArgs("hello"), CancellationToken.None);

        result.Should().Contain("Chain depth limit reached");
    }

    // ── Invocation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Returns_Json_With_Handle_And_Pending_Status()
    {
        var tracker = new FakeTracker();
        var tool = MakeTool(tracker: tracker);

        var result = await tool.InvokeAsync(MakeArgs("do something"), CancellationToken.None);

        var doc = JsonDocument.Parse(result).RootElement;
        doc.TryGetProperty("handle", out var handleEl).Should().BeTrue();
        handleEl.GetString().Should().NotBeNullOrEmpty();
        doc.GetProperty("status").GetString().Should().Be("pending");
    }

    [Fact]
    public async Task Tracker_StartAsync_Receives_Correct_AgentId_And_Message()
    {
        var tracker = new FakeTracker();
        var tool = MakeTool(tracker: tracker, effectiveAgentId: "my-agent");

        await tool.InvokeAsync(MakeArgs("test-message"), CancellationToken.None);

        tracker.LastChildAgentId.Should().Be("my-agent");
        tracker.LastMessage.Should().Be("test-message");
    }

    [Fact]
    public async Task Deterministic_Session_Ids_Are_Identical_For_Same_Arguments()
    {
        var tracker = new FakeTracker();
        var tool = MakeTool(tracker: tracker);

        using var _ = new AsyncLocalAgentContextAccessor().Push(new AgentContext { RunId = "run-42" });

        await tool.InvokeAsync(MakeArgs("same"), CancellationToken.None);
        var first = tracker.LastChildSessionId;

        await tool.InvokeAsync(MakeArgs("same"), CancellationToken.None);
        var second = tracker.LastChildSessionId;

        first.Should().Be(second);
        first.Should().NotContain("/");
    }

    [Fact]
    public async Task Different_Arguments_Produce_Different_Session_Ids()
    {
        var tracker = new FakeTracker();
        var tool = MakeTool(tracker: tracker);

        using var _ = new AsyncLocalAgentContextAccessor().Push(new AgentContext { RunId = "run-1" });

        await tool.InvokeAsync(MakeArgs("msg-a"), CancellationToken.None);
        var first = tracker.LastChildSessionId;

        await tool.InvokeAsync(MakeArgs("msg-b"), CancellationToken.None);
        var second = tracker.LastChildSessionId;

        first.Should().NotBe(second);
    }

    [Fact]
    public async Task Caller_Supplied_Session_Is_Used_When_Present()
    {
        var tracker = new FakeTracker();
        var tool = MakeTool(tracker: tracker, allowCallerSuppliedSession: true);

        using var _ = new AsyncLocalAgentContextAccessor().Push(new AgentContext { RunId = "run-99" });

        var args = JsonDocument.Parse("""{"message":"hi","sessionId":"my-session"}""").RootElement;
        await tool.InvokeAsync(args, CancellationToken.None);

        tracker.LastChildSessionId.Should().Contain("my-session");
        tracker.LastChildSessionId.Should().NotContain("/");
    }

    // ── Context propagation ──────────────────────────────────────────────────

    [Fact]
    public async Task Child_Context_Inherits_Caller_Identity_And_Decrements_Depth()
    {
        var tracker = new FakeTracker();
        var tool = MakeTool(tracker: tracker);

        using var _ = new AsyncLocalAgentContextAccessor().Push(
            new AgentContext(UserId: "alice", TenantId: "acme")
            {
                WorkspaceId = "ws-1",
                MaxChainDepth = 3,
                RunId = "run-abc",
            });

        await tool.InvokeAsync(MakeArgs("hi"), CancellationToken.None);

        var ctx = tracker.LastChildContext!;
        ctx.UserId.Should().Be("alice");
        ctx.TenantId.Should().Be("acme");
        ctx.WorkspaceId.Should().Be("ws-1");
        ctx.MaxChainDepth.Should().Be(2);
    }

    [Fact]
    public async Task AllowedTools_Propagated_When_PropagateAllowedTools_True()
    {
        var tracker = new FakeTracker();
        var tool = MakeTool(tracker: tracker, propagateAllowedTools: true);

        using var _ = new AsyncLocalAgentContextAccessor().Push(
            new AgentContext { AllowedTools = new HashSet<string>(["tool-a", "tool-b"]) });

        await tool.InvokeAsync(MakeArgs("hi"), CancellationToken.None);

        tracker.LastChildContext!.AllowedTools.Should().BeEquivalentTo(["tool-a", "tool-b"]);
    }

    [Fact]
    public async Task AllowedTools_Cleared_When_PropagateAllowedTools_False()
    {
        var tracker = new FakeTracker();
        var tool = MakeTool(tracker: tracker, propagateAllowedTools: false);

        using var _ = new AsyncLocalAgentContextAccessor().Push(
            new AgentContext { AllowedTools = new HashSet<string>(["tool-a"]) });

        await tool.InvokeAsync(MakeArgs("hi"), CancellationToken.None);

        tracker.LastChildContext!.AllowedTools.Should().BeNull();
    }

    // ── Integration: InMemoryBackgroundAgentTracker ──────────────────────────

    [Fact]
    public async Task Two_Background_SubAgents_Both_Reach_Completed()
    {
        var provider = new FakeCompletionProvider(req =>
            new CompletionResponse($"result-for:{req.History.Last().Text}"));
        var runtime = new InMemoryAgentRuntime(provider);
        var tracker = new InMemoryBackgroundAgentTracker(runtime);
        var tool = new BackgroundLocalAgentTool(
            () => runtime, tracker, "worker", "call_worker", "Fan-out worker");

        using var _ = new AsyncLocalAgentContextAccessor().Push(
            new AgentContext { RunId = "parent-run-1" });

        var r1Json = await tool.InvokeAsync(MakeArgs("task-A"), CancellationToken.None);
        var r2Json = await tool.InvokeAsync(MakeArgs("task-B"), CancellationToken.None);

        var handle1 = JsonDocument.Parse(r1Json).RootElement.GetProperty("handle").GetString()!;
        var handle2 = JsonDocument.Parse(r2Json).RootElement.GetProperty("handle").GetString()!;

        // Poll until both reach a terminal state (max ~5 s).
        var deadline = DateTime.UtcNow.AddSeconds(5);
        BackgroundAgentRunRecord? rec1 = null, rec2 = null;
        while (DateTime.UtcNow < deadline)
        {
            rec1 = await tracker.GetAsync(handle1);
            rec2 = await tracker.GetAsync(handle2);
            if (rec1 is { Status: BackgroundAgentRunStatus.Completed } &&
                rec2 is { Status: BackgroundAgentRunStatus.Completed })
                break;
            await Task.Delay(20);
        }

        rec1.Should().NotBeNull();
        rec1!.Status.Should().Be(BackgroundAgentRunStatus.Completed);
        rec1.Result.Should().Contain("task-A");

        rec2.Should().NotBeNull();
        rec2!.Status.Should().Be(BackgroundAgentRunStatus.Completed);
        rec2.Result.Should().Contain("task-B");

        // Both should be visible in the list for the parent run.
        var list = await tracker.ListAsync("parent-run-1");
        list.Should().HaveCount(2);
        list.Select(r => r.Handle).Should().BeEquivalentTo([handle1, handle2]);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static BackgroundLocalAgentTool MakeTool(
        FakeTracker? tracker = null,
        string effectiveAgentId = "test-agent",
        bool allowCallerSuppliedSession = false,
        bool propagateAllowedTools = true)
    {
        var t = tracker ?? new FakeTracker();
        var runtime = new InMemoryAgentRuntime(new FakeCompletionProvider());
        return new BackgroundLocalAgentTool(
            () => runtime,
            t,
            effectiveAgentId,
            name: "call_test_agent",
            description: "Test agent",
            allowCallerSuppliedSession: allowCallerSuppliedSession,
            propagateAllowedTools: propagateAllowedTools);
    }

    private static JsonElement MakeArgs(string message)
        => JsonDocument.Parse($$$"""{"message":"{{{message}}}"}""").RootElement;

    private sealed class FakeTracker : IBackgroundAgentTracker
    {
        public string? LastParentRunId { get; private set; }
        public string? LastChildAgentId { get; private set; }
        public string? LastChildSessionId { get; private set; }
        public string? LastMessage { get; private set; }
        public AgentContext? LastChildContext { get; private set; }

        public ValueTask<string> StartAsync(
            string parentRunId, string childAgentId, string childSessionId,
            string message, AgentContext childContext, CancellationToken ct = default)
        {
            LastParentRunId = parentRunId;
            LastChildAgentId = childAgentId;
            LastChildSessionId = childSessionId;
            LastMessage = message;
            LastChildContext = childContext;
            return ValueTask.FromResult(childSessionId);
        }

        public ValueTask<BackgroundAgentRunRecord?> GetAsync(string handle, CancellationToken ct = default)
            => ValueTask.FromResult<BackgroundAgentRunRecord?>(null);

        public ValueTask<IReadOnlyList<BackgroundAgentRunRecord>> ListAsync(string parentRunId, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<BackgroundAgentRunRecord>>(Array.Empty<BackgroundAgentRunRecord>());

        public ValueTask<bool> CancelAsync(string handle, CancellationToken ct = default)
            => ValueTask.FromResult(false);
    }
}
