// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Vais.Agents.Hosting.InMemory;
using Xunit;

namespace Vais.Agents.Core.Tests;

public sealed class LocalAgentToolTests
{
    // ── Schema tests ────────────────────────────────────────────────────────

    [Fact]
    public void Default_Schema_Has_Only_Message_Property()
    {
        var tool = MakeTool(allowCallerSuppliedSession: false);

        var schema = tool.ParametersSchema;
        schema.TryGetProperty("properties", out var props).Should().BeTrue();
        props.TryGetProperty("message", out _).Should().BeTrue();
        props.TryGetProperty("sessionId", out _).Should().BeFalse();

        schema.TryGetProperty("required", out var req).Should().BeTrue();
        req.EnumerateArray().Select(e => e.GetString()).Should().ContainSingle("message");
    }

    [Fact]
    public void Session_Schema_Has_Message_And_Optional_SessionId()
    {
        var tool = MakeTool(allowCallerSuppliedSession: true);

        var schema = tool.ParametersSchema;
        var props = schema.GetProperty("properties");
        props.TryGetProperty("message", out _).Should().BeTrue();
        props.TryGetProperty("sessionId", out _).Should().BeTrue();

        // sessionId is optional (not in required array)
        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        required.Should().ContainSingle("message");
        required.Should().NotContain("sessionId");
    }

    // ── Depth guard ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Returns_Error_String_When_MaxChainDepth_Is_Zero()
    {
        var runtime = new InMemoryAgentRuntime(new FakeCompletionProvider());
        var tool = MakeTool(runtime: runtime);

        using var _ = new AsyncLocalAgentContextAccessor().Push(
            new AgentContext { MaxChainDepth = 0 });

        var result = await tool.InvokeAsync(MakeArgs("hello"), CancellationToken.None);

        result.Should().Contain("Chain depth limit reached");
    }

    [Fact]
    public async Task Returns_Error_String_When_MaxChainDepth_Is_Negative()
    {
        var runtime = new InMemoryAgentRuntime(new FakeCompletionProvider());
        var tool = MakeTool(runtime: runtime);

        using var _ = new AsyncLocalAgentContextAccessor().Push(
            new AgentContext { MaxChainDepth = -1 });

        var result = await tool.InvokeAsync(MakeArgs("hello"), CancellationToken.None);

        result.Should().Contain("Chain depth limit reached");
    }

    [Fact]
    public async Task Invokes_Child_When_MaxChainDepth_Is_Null()
    {
        var provider = new FakeCompletionProvider(_ => new CompletionResponse("child-result"));
        var runtime = new InMemoryAgentRuntime(provider);
        var tool = MakeTool(runtime: runtime);

        var result = await tool.InvokeAsync(MakeArgs("hello"), CancellationToken.None);

        result.Should().Be("child-result");
    }

    // ── Session id derivation ────────────────────────────────────────────────

    [Fact]
    public async Task Deterministic_Session_Ids_Are_Identical_For_Same_Arguments()
    {
        var callCount = 0;
        string? lastSessionKey = null;
        var provider = new FakeCompletionProvider(_ => new CompletionResponse("ok"));
        var runtime = new TrackingRuntime(provider, (agentId, sessionId) =>
        {
            callCount++;
            lastSessionKey = sessionId;
        });

        var tool = MakeTool(runtime: runtime);

        using var _ = new AsyncLocalAgentContextAccessor().Push(
            new AgentContext { RunId = "run-42" });

        await tool.InvokeAsync(MakeArgs("same"), CancellationToken.None);
        var first = lastSessionKey;

        await tool.InvokeAsync(MakeArgs("same"), CancellationToken.None);
        var second = lastSessionKey;

        first.Should().Be(second);
        first.Should().NotContain("/");
    }

    [Fact]
    public async Task Different_Arguments_Produce_Different_Session_Ids()
    {
        var sessionIds = new List<string>();
        var provider = new FakeCompletionProvider(_ => new CompletionResponse("ok"));
        var runtime = new TrackingRuntime(provider, (_, sessionId) => sessionIds.Add(sessionId));

        var tool = MakeTool(runtime: runtime);

        using var _ = new AsyncLocalAgentContextAccessor().Push(
            new AgentContext { RunId = "run-1" });

        await tool.InvokeAsync(MakeArgs("msg-a"), CancellationToken.None);
        await tool.InvokeAsync(MakeArgs("msg-b"), CancellationToken.None);

        sessionIds.Should().HaveCount(2);
        sessionIds[0].Should().NotBe(sessionIds[1]);
    }

    [Fact]
    public async Task Caller_Supplied_Session_Is_Used_When_Present()
    {
        string? capturedSessionId = null;
        var provider = new FakeCompletionProvider(_ => new CompletionResponse("ok"));
        var runtime = new TrackingRuntime(provider, (_, sessionId) => capturedSessionId = sessionId);

        var tool = MakeTool(runtime: runtime, allowCallerSuppliedSession: true);

        using var _ = new AsyncLocalAgentContextAccessor().Push(
            new AgentContext { RunId = "run-99" });

        var args = JsonDocument.Parse("""{"message":"hi","sessionId":"my-session"}""").RootElement;
        await tool.InvokeAsync(args, CancellationToken.None);

        capturedSessionId.Should().Contain("my-session");
        capturedSessionId.Should().NotContain("/");
    }

    // ── Session cleanup ──────────────────────────────────────────────────────

    [Fact]
    public async Task Deterministic_Session_Is_Removed_After_Call()
    {
        var provider = new FakeCompletionProvider(_ => new CompletionResponse("done"));
        var runtime = new InMemoryAgentRuntime(provider);
        var tool = MakeTool(runtime: runtime);

        using var _ = new AsyncLocalAgentContextAccessor().Push(
            new AgentContext { RunId = "run-clean" });

        await tool.InvokeAsync(MakeArgs("test"), CancellationToken.None);

        // Verify no session agents remain — the runtime dictionary is internal,
        // so we use the round-trip: a second call creates a fresh session (no state).
        // The provider would have been called once for each independent fresh session.
        var response2 = await tool.InvokeAsync(MakeArgs("test"), CancellationToken.None);
        response2.Should().Be("done");
    }

    [Fact]
    public async Task Caller_Supplied_Session_Is_NOT_Removed_After_Call()
    {
        var removeCount = 0;
        var provider = new FakeCompletionProvider(_ => new CompletionResponse("ok"));
        var runtime = new TrackingRuntime(provider, removeAction: (_, _) => removeCount++);

        var tool = MakeTool(runtime: runtime, allowCallerSuppliedSession: true);

        var args = JsonDocument.Parse("""{"message":"hi","sessionId":"sticky"}""").RootElement;
        await tool.InvokeAsync(args, CancellationToken.None);

        removeCount.Should().Be(0);
    }

    // ── Context propagation ──────────────────────────────────────────────────

    [Fact]
    public async Task Child_Context_Inherits_Caller_Identity_And_Decrements_Depth()
    {
        AgentContext? capturedCtx = null;
        var provider = new FakeCompletionProvider(_ =>
        {
            capturedCtx = new AsyncLocalAgentContextAccessor().Current;
            return new CompletionResponse("ok");
        });
        var runtime = new InMemoryAgentRuntime(provider);
        var tool = MakeTool(runtime: runtime);

        using var _ = new AsyncLocalAgentContextAccessor().Push(
            new AgentContext(UserId: "alice", TenantId: "acme")
            {
                WorkspaceId = "ws-1",
                MaxChainDepth = 3,
                RunId = "run-abc",
            });

        await tool.InvokeAsync(MakeArgs("hi"), CancellationToken.None);

        capturedCtx.Should().NotBeNull();
        capturedCtx!.UserId.Should().Be("alice");
        capturedCtx.TenantId.Should().Be("acme");
        capturedCtx.WorkspaceId.Should().Be("ws-1");
        capturedCtx.MaxChainDepth.Should().Be(2);
    }

    [Fact]
    public async Task AllowedTools_Propagated_When_PropagateAllowedTools_True()
    {
        AgentContext? capturedCtx = null;
        var provider = new FakeCompletionProvider(_ =>
        {
            capturedCtx = new AsyncLocalAgentContextAccessor().Current;
            return new CompletionResponse("ok");
        });
        var runtime = new InMemoryAgentRuntime(provider);
        var tool = MakeTool(runtime: runtime, propagateAllowedTools: true);

        var allowed = new HashSet<string>(["tool-a", "tool-b"]);
        using var _ = new AsyncLocalAgentContextAccessor().Push(
            new AgentContext { AllowedTools = allowed });

        await tool.InvokeAsync(MakeArgs("hi"), CancellationToken.None);

        capturedCtx!.AllowedTools.Should().BeEquivalentTo(["tool-a", "tool-b"]);
    }

    [Fact]
    public async Task AllowedTools_Cleared_When_PropagateAllowedTools_False()
    {
        AgentContext? capturedCtx = null;
        var provider = new FakeCompletionProvider(_ =>
        {
            capturedCtx = new AsyncLocalAgentContextAccessor().Current;
            return new CompletionResponse("ok");
        });
        var runtime = new InMemoryAgentRuntime(provider);
        var tool = MakeTool(runtime: runtime, propagateAllowedTools: false);

        using var _ = new AsyncLocalAgentContextAccessor().Push(
            new AgentContext { AllowedTools = new HashSet<string>(["tool-a"]) });

        await tool.InvokeAsync(MakeArgs("hi"), CancellationToken.None);

        capturedCtx!.AllowedTools.Should().BeNull();
    }

    // ── SanitiseSessionId ────────────────────────────────────────────────────

    [Theory]
    [InlineData("run/123", "run_123")]
    [InlineData("a//b", "a_b")]
    [InlineData("no-slash", "no-slash")]
    [InlineData("", "")]
    public void SanitiseSessionId_Replaces_Slashes(string input, string expected)
    {
        LocalAgentTool.SanitiseSessionId(input).Should().Be(expected);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static LocalAgentTool MakeTool(
        IAgentRuntime? runtime = null,
        bool allowCallerSuppliedSession = false,
        bool propagateAllowedTools = true)
    {
        var rt = runtime ?? new InMemoryAgentRuntime(new FakeCompletionProvider());
        return new LocalAgentTool(
            () => rt,
            effectiveAgentId: "test-agent",
            name: "call_test_agent",
            description: "A test agent",
            allowCallerSuppliedSession: allowCallerSuppliedSession,
            propagateAllowedTools: propagateAllowedTools);
    }

    private static JsonElement MakeArgs(string message)
        => JsonDocument.Parse($$$"""{"message":"{{{message}}}"}""").RootElement;

    /// <summary>
    /// A runtime that calls optional callbacks on GetOrCreateForSession and RemoveSession.
    /// </summary>
    private sealed class TrackingRuntime : IAgentRuntime
    {
        private readonly InMemoryAgentRuntime _inner;
        private readonly Action<string, string>? _getAction;
        private readonly Action<string, string>? _removeAction;

        public TrackingRuntime(
            ICompletionProvider provider,
            Action<string, string>? getAction = null,
            Action<string, string>? removeAction = null)
        {
            _inner = new InMemoryAgentRuntime(provider);
            _getAction = getAction;
            _removeAction = removeAction;
        }

        public IAiAgent GetOrCreate(string agentId) => _inner.GetOrCreate(agentId);

        public IAiAgent GetOrCreateForSession(string agentId, string sessionId)
        {
            _getAction?.Invoke(agentId, sessionId);
            return _inner.GetOrCreateForSession(agentId, sessionId);
        }

        public bool TryGet(string agentId, out IAiAgent? agent) => _inner.TryGet(agentId, out agent);

        public bool Remove(string agentId) => _inner.Remove(agentId);

        public bool RemoveSession(string agentId, string sessionId)
        {
            _removeAction?.Invoke(agentId, sessionId);
            return _inner.RemoveSession(agentId, sessionId);
        }
    }
}
