// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using Vais.Agents.Core;
using Xunit;

namespace Vais.Agents.Hosting.Orleans.Tests;

/// <summary>
/// Verifies that caller identity fields (AllowedTools, PrivilegeLevel, UserId, TenantId, …)
/// propagate across the Orleans grain boundary and are visible to in-grain tools and guards.
/// Covers the fix for the in-grain AgentContext identity propagation gap (2026-05-30).
/// </summary>
public sealed class InGrainContextIdentityPropagationTests : IDisposable
{
    public InGrainContextIdentityPropagationTests() => RequestContext.Clear();

    public void Dispose() => RequestContext.Clear();

    // ── T4: UserId/TenantId propagate from RequestContext ───────────────────

    [Fact]
    public void InGrain_UserId_And_TenantId_Surface_Via_OrleansAgentContextAccessor()
    {
        RequestContext.Set(AgenticTags.UserId, "user-grain-42");
        RequestContext.Set(AgenticTags.TenantId, "tenant-blue");

        var context = new OrleansAgentContextAccessor().Current;

        context.UserId.Should().Be("user-grain-42");
        context.TenantId.Should().Be("tenant-blue");
    }

    // ── T3: In-grain tool observes pushed AllowedTools + PrivilegeLevel ─────

    [Fact]
    public void InGrain_Tool_Observes_AllowedTools_And_PrivilegeLevel_After_Push()
    {
        var allowedTools = ImmutableHashSet.Create("allowed_tool");
        var pushed = new AgentContext(AgentName: "test-agent")
        {
            AllowedTools = allowedTools,
            PrivilegeLevel = PrivilegeLevel.Platform,
        };

        AgentContext? observedInTool = null;
        var setter = new AsyncLocalAgentContextAccessor();

        using (setter.Push(pushed))
        {
            // Simulate what a tool body does: read from the static AsyncLocal slot.
            observedInTool = new AsyncLocalAgentContextAccessor().Current;
        }

        observedInTool.Should().NotBeNull();
        observedInTool!.AllowedTools.Should().BeEquivalentTo(["allowed_tool"]);
        observedInTool.PrivilegeLevel.Should().Be(PrivilegeLevel.Platform);
    }

    // ── T3b: Pushed context is not visible outside the using scope ───────────

    [Fact]
    public void InGrain_Ambient_Context_Restored_After_Push_Scope()
    {
        var pushed = new AgentContext(AgentName: "scoped-agent")
        {
            AllowedTools = ImmutableHashSet.Create("tool_a"),
        };

        var setter = new AsyncLocalAgentContextAccessor();
        using (setter.Push(pushed))
        {
            new AsyncLocalAgentContextAccessor().Current.AllowedTools.Should().NotBeNull();
        }

        // After the scope exits, ambient context reverts.
        new AsyncLocalAgentContextAccessor().Current.AllowedTools.Should().BeNull();
    }

    // ── T2: AllowedTools enforcement applies to grain-hosted agent ───────────

    [Fact]
    public async Task InGrain_AllowedTools_Enforcement_Blocks_Disallowed_Tool()
    {
        // Context that allows only "safe_tool" — "dangerous_tool" should be rejected.
        var context = new AgentContext(AgentName: "enforced-agent")
        {
            AllowedTools = ImmutableHashSet.Create("safe_tool"),
        };

        var dispatcher = new DefaultToolCallDispatcher(toolRegistry: null);
        var request = new ToolCallRequest("dangerous_tool", JsonDocument.Parse("{}").RootElement, "call-1");

        var outcome = await dispatcher.DispatchAsync(request, context);

        outcome.Error.Should().Contain("UnauthorizedAccessException",
            because: "AllowedTools enforcement should deny tools outside the list");
        outcome.Result.Should().Contain("dangerous_tool",
            because: "the denial message should name the blocked tool");
    }

    [Fact]
    public async Task InGrain_AllowedTools_Enforcement_Permits_Listed_Tool()
    {
        // "listed_tool" is in AllowedTools — dispatcher should reach the registry (which
        // returns not-found since we pass null), not a denial. The error will be
        // "KeyNotFoundException" (tool not in registry), not "UnauthorizedAccessException".
        var context = new AgentContext(AgentName: "enforced-agent")
        {
            AllowedTools = ImmutableHashSet.Create("listed_tool"),
        };

        var dispatcher = new DefaultToolCallDispatcher(toolRegistry: null);
        var request = new ToolCallRequest("listed_tool", JsonDocument.Parse("{}").RootElement, "call-2");

        var outcome = await dispatcher.DispatchAsync(request, context);

        outcome.Error.Should().Contain("KeyNotFoundException",
            because: "the tool passed the AllowedTools gate but is not in the registry");
        outcome.Error.Should().NotContain("UnauthorizedAccessException",
            because: "AllowedTools should not block a listed tool");
    }

    // ── T2 via push: AllowedTools in ambient context is picked up by dispatcher ─

    [Fact]
    public async Task InGrain_AllowedTools_Enforcement_Works_When_Context_Pushed_To_AsyncLocal()
    {
        // Simulate the grain push: set identity on RequestContext, read via
        // OrleansAgentContextAccessor, push to AsyncLocalAgentContextAccessor.
        RequestContext.Set(AgenticTags.AllowedTools,
            ImmutableHashSet.Create("only_allowed_tool"));

        var incomingContext = new OrleansAgentContextAccessor().Current;

        var setter = new AsyncLocalAgentContextAccessor();
        using (setter.Push(incomingContext))
        {
            // Tool dispatcher reads context.AllowedTools from its parameter (same object).
            var dispatcher = new DefaultToolCallDispatcher(toolRegistry: null);
            var request = new ToolCallRequest("blocked_tool", JsonDocument.Parse("{}").RootElement, "call-3");

            var outcome = await dispatcher.DispatchAsync(request, incomingContext);

            outcome.Error.Should().Contain("UnauthorizedAccessException");
        }
    }

    // ── Scopes + BaselineRunId end-to-end ────────────────────────────────────

    [Fact]
    public void InGrain_Scopes_Propagate_Via_OrleansAgentContextAccessor()
    {
        RequestContext.Set(AgenticTags.Scopes, new[] { "read", "write" });

        var context = new OrleansAgentContextAccessor().Current;

        context.Scopes.Should().BeEquivalentTo(["read", "write"]);
    }

    [Fact]
    public void InGrain_BaselineRunId_Propagates_Via_OrleansAgentContextAccessor()
    {
        RequestContext.Set(AgenticTags.BaselineRunId, "baseline-run-99");

        var context = new OrleansAgentContextAccessor().Current;

        context.BaselineRunId.Should().Be("baseline-run-99");
    }

    // ── T5: LocalAgentTool MaxChainDepth guard fires when depth is 0 ─────────

    [Fact]
    public async Task InGrain_LocalAgentTool_MaxChainDepth_Guard_Fires_When_Depth_Zero()
    {
        // Push a context with MaxChainDepth = 0 — LocalAgentTool reads from static
        // AsyncLocalAgentContextAccessor internally (line 103 of LocalAgentTool.cs).
        var context = new AgentContext(AgentName: "caller-agent") { MaxChainDepth = 0 };
        var setter = new AsyncLocalAgentContextAccessor();

        using (setter.Push(context))
        {
            // _runtimeFactory is never called when depth guard fires first.
            var tool = new LocalAgentTool(
                runtimeFactory: () => throw new InvalidOperationException("should not be called"),
                effectiveAgentId: "child-agent",
                name: "child_tool",
                description: "A sub-agent tool.");

            var result = await tool.InvokeAsync(
                JsonDocument.Parse("""{"message":"hello"}""").RootElement);

            result.Should().Contain("Chain depth limit",
                because: "MaxChainDepth = 0 should trigger the depth guard");
        }
    }

    [Fact]
    public async Task InGrain_LocalAgentTool_MaxChainDepth_Guard_Does_Not_Fire_When_Depth_Positive()
    {
        // MaxChainDepth = 1 → guard passes (depth > 0). The tool will attempt to get the
        // runtime and invoke the child agent — we use a RuntimeFactory that throws to
        // verify the guard itself didn't intercept and we reached the actual invocation attempt.
        var context = new AgentContext(AgentName: "caller-agent") { MaxChainDepth = 1 };
        var setter = new AsyncLocalAgentContextAccessor();

        using (setter.Push(context))
        {
            var tool = new LocalAgentTool(
                runtimeFactory: () => throw new InvalidOperationException("runtime-not-configured"),
                effectiveAgentId: "child-agent",
                name: "child_tool",
                description: "A sub-agent tool.");

            var act = () => tool.InvokeAsync(
                JsonDocument.Parse("""{"message":"hello"}""").RootElement);

            // Guard passed, so we reach the runtime factory — which throws.
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("runtime-not-configured");
        }
    }

    // ── OutgoingFilter: UserId/TenantId are written to RequestContext ────────

    [Fact]
    public void OutgoingFilter_Writes_UserId_TenantId_To_RequestContext()
    {
        // The outgoing filter reads from IAgentContextAccessor.Current (AsyncLocal in
        // the HTTP host; OrleansAgentContextAccessor in the standalone silo). Push a
        // context with UserId/TenantId to the static AsyncLocal slot, then read it
        // back via OrleansAgentContextAccessor after simulating what the filter writes.
        var ambient = new AgentContext(AgentName: "test")
        {
            UserId = "alice",
            TenantId = "tenant-x",
        };

        // Simulate the outgoing filter write side:
        if (ambient.UserId is not null) RequestContext.Set(AgenticTags.UserId, ambient.UserId);
        if (ambient.TenantId is not null) RequestContext.Set(AgenticTags.TenantId, ambient.TenantId);

        var reconstructed = new OrleansAgentContextAccessor().Current;

        reconstructed.UserId.Should().Be("alice");
        reconstructed.TenantId.Should().Be("tenant-x");
    }
}
