// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Vais.Agents.Gateways.Governance;
using Xunit;

namespace Vais.Agents.Gateways.McpGovernance.Tests;

public sealed class McpGovernanceTests
{
    private static readonly JsonElement EmptyArgs = JsonDocument.Parse("{}").RootElement;

    private static ToolGatewayContext MakeContext(string toolName = "tool", string callId = "c1",
        string? workspaceId = null, PrivilegeLevel? privilegeLevel = null)
        => new(toolName, callId, EmptyArgs,
            new AgentContext() { WorkspaceId = workspaceId, PrivilegeLevel = privilegeLevel });

    // ── ToolRateLimitMiddleware ──────────────────────────────────────────────

    [Fact]
    public async Task RateLimit_Within_Limit_Passes_Through()
    {
        var store = new InMemorySlidingWindowRateLimitStore();
        var options = new ToolRateLimitOptions { MaxRequestsPerWindow = 5, Window = TimeSpan.FromMinutes(1) };
        var mw = new ToolRateLimitMiddleware(store, options);
        var ctx = MakeContext(workspaceId: "ws-ok");
        Func<Task<ToolCallOutcome>> next = () => Task.FromResult(new ToolCallOutcome("c1", "ok"));

        var outcome = await mw.InvokeAsync(ctx, next, CancellationToken.None);

        outcome.Error.Should().BeNull();
    }

    [Fact]
    public async Task RateLimit_Over_Limit_Returns_RateLimitExceeded()
    {
        var store = new InMemorySlidingWindowRateLimitStore();
        var options = new ToolRateLimitOptions { MaxRequestsPerWindow = 2, Window = TimeSpan.FromMinutes(1) };
        var mw = new ToolRateLimitMiddleware(store, options);
        var ctx = MakeContext(workspaceId: "ws-limited");
        Func<Task<ToolCallOutcome>> next = () => Task.FromResult(new ToolCallOutcome("c1", "ok"));

        // Exhaust the limit
        await mw.InvokeAsync(ctx, next, CancellationToken.None);
        await mw.InvokeAsync(ctx, next, CancellationToken.None);

        // Third call should be rate-limited
        var outcome = await mw.InvokeAsync(ctx, next, CancellationToken.None);

        outcome.Error.Should().Be("ToolRateLimitExceeded");
    }

    // ── WorkspaceToolPolicy ──────────────────────────────────────────────────

    [Fact]
    public void WorkspacePolicy_Allow_Empty_AllowedPrefixes_Permits_All_Non_Denied()
    {
        var policy = new WorkspaceToolPolicy(AllowedPrefixes: [], DeniedPrefixes: ["bad_"]);
        policy.IsAllowed("good_tool", null).Should().BeTrue();
        policy.IsAllowed("bad_tool", null).Should().BeFalse();
    }

    [Fact]
    public void WorkspacePolicy_Allow_Prefix_Allows_Matching_Tool()
    {
        var policy = new WorkspaceToolPolicy(
            AllowedPrefixes: ["safe_"],
            DeniedPrefixes: []);
        policy.IsAllowed("safe_search", null).Should().BeTrue();
        policy.IsAllowed("unsafe_delete", null).Should().BeFalse();
    }

    [Fact]
    public void WorkspacePolicy_Privilege_Level_Below_Minimum_Denies()
    {
        // MinPrivilegeLevel=1 (Workspace). Agent=2 (numerically higher, less privileged).
        var policy = new WorkspaceToolPolicy(
            AllowedPrefixes: [],
            DeniedPrefixes: [],
            MinPrivilegeLevel: 1);
        policy.IsAllowed("any_tool", PrivilegeLevel.Agent).Should().BeFalse();
        policy.IsAllowed("any_tool", PrivilegeLevel.Platform).Should().BeTrue();
    }

    // ── ToolWorkspacePolicyMiddleware ────────────────────────────────────────

    [Fact]
    public async Task WorkspacePolicy_Denied_Tool_Returns_ToolDenied()
    {
        var policies = new Dictionary<string, WorkspaceToolPolicy>
        {
            ["ws-restricted"] = new WorkspaceToolPolicy(
                AllowedPrefixes: [],
                DeniedPrefixes: ["admin_"]),
        };
        var mw = new ToolWorkspacePolicyMiddleware(policies);
        var ctx = MakeContext("admin_delete", "c1", "ws-restricted");
        Func<Task<ToolCallOutcome>> next = () => Task.FromResult(new ToolCallOutcome("c1", "ok"));

        var outcome = await mw.InvokeAsync(ctx, next, CancellationToken.None);

        outcome.Error.Should().Be("ToolDenied");
    }

    [Fact]
    public async Task WorkspacePolicy_Allowed_Tool_Passes_Through()
    {
        var policies = new Dictionary<string, WorkspaceToolPolicy>
        {
            ["ws-restricted"] = new WorkspaceToolPolicy(
                AllowedPrefixes: ["safe_"],
                DeniedPrefixes: []),
        };
        var mw = new ToolWorkspacePolicyMiddleware(policies);
        var ctx = MakeContext("safe_search", "c1", "ws-restricted");
        Func<Task<ToolCallOutcome>> next = () => Task.FromResult(new ToolCallOutcome("c1", "ok"));

        var outcome = await mw.InvokeAsync(ctx, next, CancellationToken.None);

        outcome.Error.Should().BeNull();
    }

    [Fact]
    public async Task WorkspacePolicy_No_Policy_For_Workspace_Passes_Through()
    {
        var mw = new ToolWorkspacePolicyMiddleware(new Dictionary<string, WorkspaceToolPolicy>());
        var ctx = MakeContext("any_tool", "c1", "unknown-ws");
        Func<Task<ToolCallOutcome>> next = () => Task.FromResult(new ToolCallOutcome("c1", "ok"));

        var outcome = await mw.InvokeAsync(ctx, next, CancellationToken.None);

        outcome.Error.Should().BeNull();
    }
}
