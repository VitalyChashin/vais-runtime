// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Core.Tests.Ontology;

/// <summary>
/// C2-5 verify: the delegation-governance middleware runs IDelegationPolicy against
/// sub-agent calls (capability-map membership), short-circuits with a structured
/// {ok:false, reason, suggestions[]} outcome on deny, and passes regular tool calls
/// through unchanged. Pass-through when the host hasn't populated AgentContext.AgentName
/// (headless / library scenarios).
/// </summary>
public sealed class DelegationGovernanceMiddlewareTests
{
    private const string Coord = "coordinator";

    private static readonly CapabilityMap Map = new(Coord, [
        new SubAgentCapability("reviewer", "code-reviewer", "Reviews code.", ["role:review"], LocalAgentInvocationMode.Blocking),
        new SubAgentCapability("deployer", "deploy-bot", "Deploys to dev.", ["role:ops", "risk:Destructive"], LocalAgentInvocationMode.Blocking),
    ]);

    [Fact]
    public async Task PassesThroughRegularToolCall_NotInMap()
    {
        var mw = new DelegationGovernanceMiddleware(new RecordingPolicy(_ => DelegationDecision.Allow), MapBuilder.Static(Map));
        var ctx = NewContext(toolName: "mcp.search"); // not a sub-agent
        var nextCalled = false;

        var outcome = await mw.InvokeAsync(ctx, () => { nextCalled = true; return Task.FromResult(new ToolCallOutcome("c1", "ok")); });

        nextCalled.Should().BeTrue();
        outcome.Result.Should().Be("ok");
    }

    [Fact]
    public async Task ConsultsPolicyForSubAgentCall_AllowsPassThrough()
    {
        var policy = new RecordingPolicy(_ => DelegationDecision.Allow);
        var mw = new DelegationGovernanceMiddleware(policy, MapBuilder.Static(Map));
        var ctx = NewContext(toolName: "reviewer");
        var nextCalled = false;

        var outcome = await mw.InvokeAsync(ctx, () => { nextCalled = true; return Task.FromResult(new ToolCallOutcome("c1", "ok")); });

        policy.EvaluatedToolNames.Should().ContainSingle().Which.Should().Be("reviewer");
        nextCalled.Should().BeTrue();
        outcome.Result.Should().Be("ok");
    }

    [Fact]
    public async Task DeniedDelegation_ShortCircuitsWithStructuredFailureBeforeNext()
    {
        var policy = new RecordingPolicy(ctx => DelegationDecision.Deny(
            $"{ctx.ToolName} requires prior 'reviewer' approval",
            ["call 'reviewer' first and ensure its result was approved"]));
        var mw = new DelegationGovernanceMiddleware(policy, MapBuilder.Static(Map));
        var ctx = NewContext(toolName: "deployer");
        var nextCalled = false;

        var outcome = await mw.InvokeAsync(ctx, () => { nextCalled = true; return Task.FromResult(new ToolCallOutcome("c1", "deployed!")); });

        nextCalled.Should().BeFalse("denial short-circuits before upstream invocation");
        var root = JsonDocument.Parse(outcome.Result!).RootElement;
        root.GetProperty("ok").GetBoolean().Should().BeFalse();
        root.GetProperty("reason").GetString().Should().Contain("deployer requires prior 'reviewer' approval");
        root.GetProperty("suggestions").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task PassesThroughWhenCoordinatorAgentNameIsUnset()
    {
        var policy = new RecordingPolicy(_ => DelegationDecision.Deny("would-deny-if-asked"));
        var mw = new DelegationGovernanceMiddleware(policy, MapBuilder.Static(Map));
        var ctx = NewContext(toolName: "reviewer", agentName: null);
        var nextCalled = false;

        var outcome = await mw.InvokeAsync(ctx, () => { nextCalled = true; return Task.FromResult(new ToolCallOutcome("c1", "ok")); });

        nextCalled.Should().BeTrue("no coordinator id ⇒ pass-through; the policy is not consulted");
        policy.EvaluatedToolNames.Should().BeEmpty();
        outcome.Result.Should().Be("ok");
    }

    [Fact]
    public void Middleware_DeclaresValidationKind()
    {
        var mw = new DelegationGovernanceMiddleware(AllowAllDelegationPolicy.Instance, MapBuilder.Static(Map));
        ((OntologyInterceptor)mw).Kind.Should().Be(InterceptorKind.Validation);
    }

    [Fact]
    public async Task AllowAllPolicy_IsAlwaysAllow()
    {
        var decision = await AllowAllDelegationPolicy.Instance.EvaluateAsync(NewContext("x"), Map);
        decision.Allowed.Should().BeTrue();
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static ToolGatewayContext NewContext(string toolName, string? agentName = Coord) =>
        new(
            ToolName: toolName,
            CallId: "call-1",
            Arguments: JsonDocument.Parse("{}").RootElement,
            AgentContext: agentName is null
                ? AgentContext.Empty
                : AgentContext.Empty with { AgentName = agentName });

    private sealed class RecordingPolicy(Func<ToolGatewayContext, DelegationDecision> decide) : IDelegationPolicy
    {
        public List<string> EvaluatedToolNames { get; } = new();

        public ValueTask<DelegationDecision> EvaluateAsync(ToolGatewayContext context, CapabilityMap map, CancellationToken cancellationToken = default)
        {
            EvaluatedToolNames.Add(context.ToolName);
            return ValueTask.FromResult(decide(context));
        }
    }

    private static class MapBuilder
    {
        public static IAgentCapabilityMapBuilder Static(CapabilityMap map) => new StaticBuilder(map);

        private sealed class StaticBuilder(CapabilityMap map) : IAgentCapabilityMapBuilder
        {
            public ValueTask<CapabilityMap> BuildAsync(string coordinatorAgentId, CancellationToken cancellationToken = default)
                => new(map);
            public void Invalidate(string coordinatorAgentId) { }
        }
    }
}
