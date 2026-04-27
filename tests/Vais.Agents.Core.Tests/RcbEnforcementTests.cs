// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// Tests for RCB field additions to <see cref="AgentContext"/> and the
/// <see cref="DefaultToolCallDispatcher"/> AllowedTools enforcement.
/// </summary>
public sealed class RcbEnforcementTests
{
    // ── AgentContext.Empty backward compatibility ─────────────────────────────

    [Fact]
    public void AgentContext_Empty_Has_All_Rcb_Fields_Null()
    {
        var ctx = AgentContext.Empty;
        ctx.WorkspaceId.Should().BeNull();
        ctx.PrivilegeLevel.Should().BeNull();
        ctx.AutonomyLevel.Should().BeNull();
        ctx.AllowedTools.Should().BeNull();
        ctx.MaxChainDepth.Should().BeNull();
    }

    [Fact]
    public void AgentContext_Constructor_Without_Rcb_Fields_Compiles_And_Leaves_Rcb_Null()
    {
        var ctx = new AgentContext(UserId: "alice", TenantId: "acme");
        ctx.UserId.Should().Be("alice");
        ctx.TenantId.Should().Be("acme");
        ctx.WorkspaceId.Should().BeNull();
        ctx.PrivilegeLevel.Should().BeNull();
        ctx.AllowedTools.Should().BeNull();
    }

    // ── with-expression non-destructive mutation ──────────────────────────────

    [Fact]
    public void With_Expression_Sets_New_Rcb_Field_Leaving_Others_Unchanged()
    {
        var original = new AgentContext(UserId: "alice")
        {
            WorkspaceId = "ws-1",
            AllowedTools = ImmutableHashSet.Create("search"),
        };

        var narrowed = original with { AllowedTools = ImmutableHashSet.Create("search", "read") };

        narrowed.UserId.Should().Be("alice");
        narrowed.WorkspaceId.Should().Be("ws-1");      // unchanged
        narrowed.AllowedTools.Should().BeEquivalentTo(new[] { "search", "read" });
    }

    [Fact]
    public void With_Expression_On_Empty_Sets_Only_The_Specified_Field()
    {
        var ctx = AgentContext.Empty with { WorkspaceId = "ws-42" };
        ctx.WorkspaceId.Should().Be("ws-42");
        ctx.UserId.Should().BeNull();
        ctx.AllowedTools.Should().BeNull();
    }

    // ── AllowedTools enforcement in DefaultToolCallDispatcher ─────────────────

    [Fact]
    public async Task Dispatcher_Allows_Tool_When_AllowedTools_Is_Null()
    {
        var tool = new FakeTool("search", _ => "result");
        var dispatcher = new DefaultToolCallDispatcher(new InMemoryToolRegistry(tool));
        var call = new ToolCallRequest("search", JsonDocument.Parse("{}").RootElement, "c1");

        // null AllowedTools = no restriction
        var outcome = await dispatcher.DispatchAsync(call, AgentContext.Empty);

        outcome.Error.Should().BeNull();
        outcome.Result.Should().Be("result");
    }

    [Fact]
    public async Task Dispatcher_Allows_Tool_When_It_Is_In_AllowedTools()
    {
        var tool = new FakeTool("search", _ => "found");
        var dispatcher = new DefaultToolCallDispatcher(new InMemoryToolRegistry(tool));
        var call = new ToolCallRequest("search", JsonDocument.Parse("{}").RootElement, "c2");
        var ctx = AgentContext.Empty with { AllowedTools = ImmutableHashSet.Create("search", "read") };

        var outcome = await dispatcher.DispatchAsync(call, ctx);

        outcome.Error.Should().BeNull();
        outcome.Result.Should().Be("found");
    }

    [Fact]
    public async Task Dispatcher_Rejects_Tool_Not_In_AllowedTools()
    {
        var tool = new FakeTool("delete", _ => "deleted");
        var dispatcher = new DefaultToolCallDispatcher(new InMemoryToolRegistry(tool));
        var call = new ToolCallRequest("delete", JsonDocument.Parse("{}").RootElement, "c3");
        var ctx = AgentContext.Empty with { AllowedTools = ImmutableHashSet.Create("search", "read") };

        var outcome = await dispatcher.DispatchAsync(call, ctx);

        outcome.Error.Should().Be(nameof(UnauthorizedAccessException));
        outcome.Result.Should().Contain("delete");
        outcome.Result.Should().Contain("allowed-tools");
    }

    [Fact]
    public async Task Dispatcher_Rejects_All_Tools_When_AllowedTools_Is_Empty()
    {
        var tool = new FakeTool("search", _ => "result");
        var dispatcher = new DefaultToolCallDispatcher(new InMemoryToolRegistry(tool));
        var call = new ToolCallRequest("search", JsonDocument.Parse("{}").RootElement, "c4");
        var ctx = AgentContext.Empty with { AllowedTools = ImmutableHashSet<string>.Empty };

        var outcome = await dispatcher.DispatchAsync(call, ctx);

        outcome.Error.Should().Be(nameof(UnauthorizedAccessException));
    }

    [Fact]
    public async Task Dispatcher_AllowedTools_Check_Does_Not_Journal_Denied_Calls()
    {
        // Denied calls must not be journaled — they did not produce a reproducible result.
        var journal = new InMemoryAgentJournal();
        var tool = new FakeTool("secret", _ => "sensitive");
        var dispatcher = new DefaultToolCallDispatcher(
            new InMemoryToolRegistry(tool),
            journal: journal);
        var call = new ToolCallRequest("secret", JsonDocument.Parse("{}").RootElement, "c5");
        var ctx = new AgentContext { RunId = "run-1", AllowedTools = ImmutableHashSet.Create("safe") };

        await dispatcher.DispatchAsync(call, ctx);

        var entries = new List<JournalEntry>();
        await foreach (var e in journal.ReadAsync("run-1"))
        {
            entries.Add(e);
        }
        entries.Should().BeEmpty();
    }

    // ── Enum ordering contract ────────────────────────────────────────────────

    [Fact]
    public void PrivilegeLevel_Numeric_Order_Platform_Lowest_Int()
    {
        // Higher int = lower privilege; Math.Max(caller, callee) → most restrictive wins.
        ((int)PrivilegeLevel.Platform).Should().BeLessThan((int)PrivilegeLevel.Workspace);
        ((int)PrivilegeLevel.Workspace).Should().BeLessThan((int)PrivilegeLevel.Agent);
    }

    [Fact]
    public void AutonomyLevel_Numeric_Order_FullyAutonomous_Lowest_Int()
    {
        ((int)AutonomyLevel.FullyAutonomous).Should().BeLessThan((int)AutonomyLevel.Supervised);
        ((int)AutonomyLevel.Supervised).Should().BeLessThan((int)AutonomyLevel.RequiresApproval);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class InMemoryToolRegistry(params ITool[] tools) : IToolRegistry
    {
        public IReadOnlyList<ITool> Tools { get; } = tools;
        public ITool? GetByName(string name) => Tools.FirstOrDefault(t => t.Name == name);
    }

    private sealed class FakeTool(string name, Func<JsonElement, string> invoke) : ITool
    {
        public string Name => name;
        public string Description => "fake";
        public JsonElement ParametersSchema { get; } = JsonDocument.Parse("{\"type\":\"object\"}").RootElement;
        public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
            => Task.FromResult(invoke(arguments));
    }
}
