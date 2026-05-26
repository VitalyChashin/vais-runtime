// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Runtime.Instantiation.Tests;

/// <summary>
/// Plan C2 follow-on (FU-1 + FU-2): when a coordinator has <see cref="AgentManifest.LocalAgents"/>
/// and an <see cref="IAgentCapabilityMapBuilder"/> is registered, the translator auto-wires
/// <see cref="CapabilityMapInputMiddleware"/> onto the per-agent input chain. When a
/// non-default <see cref="IDelegationPolicy"/> is also registered (or the default Allow-all),
/// the translator appends <see cref="DelegationGovernanceMiddleware"/> innermost on the
/// tool-gateway chain. Agents without local-agents are untouched.
/// </summary>
public sealed class CapabilityFabricAutoWiringTests
{
    [Fact]
    public async Task Coordinator_WithLocalAgents_GetsCapabilityMapInputMiddleware()
    {
        var translator = NewFixture()
            .WithManifest(BuildCoordinator("coord", subTool: "reviewer", subAgentId: "code-reviewer"))
            .WithManifest(BuildSubAgent("code-reviewer", "Reviews code."))
            .Translator;

        var options = await translator.TranslateAsync("coord");

        options.InputMiddleware.Should().ContainSingle()
            .Which.Should().BeOfType<CapabilityMapInputMiddleware>();
    }

    [Fact]
    public async Task Coordinator_WithLocalAgentsAndPolicy_AlsoGetsDelegationGovernance()
    {
        var translator = NewFixture()
            .WithDelegationPolicy(AllowAllDelegationPolicy.Instance)
            .WithManifest(BuildCoordinator("coord", subTool: "reviewer", subAgentId: "code-reviewer"))
            .WithManifest(BuildSubAgent("code-reviewer", "Reviews code."))
            .Translator;

        var options = await translator.TranslateAsync("coord");

        options.ToolGatewayMiddleware.Should().ContainSingle()
            .Which.Should().BeOfType<DelegationGovernanceMiddleware>();
    }

    [Fact]
    public async Task Coordinator_WithoutLocalAgents_HasNoCapabilityFabricMiddleware()
    {
        var translator = NewFixture()
            .WithDelegationPolicy(AllowAllDelegationPolicy.Instance)
            .WithManifest(BuildLonelyAgent("lonely"))
            .Translator;

        var options = await translator.TranslateAsync("lonely");

        options.InputMiddleware.Should().BeEmpty("no local-agents ⇒ no capability map to inject");
        options.ToolGatewayMiddleware.OfType<DelegationGovernanceMiddleware>().Should().BeEmpty(
            "no local-agents ⇒ no delegation to govern");
    }

    [Fact]
    public async Task Coordinator_WithLocalAgentsButNoPolicy_SkipsDelegationGovernance()
    {
        // Capability-map builder is registered (via fixture default), but no IDelegationPolicy:
        // the input-middleware lands, the governance middleware does not (no policy to consult).
        var translator = NewFixture()
            .WithManifest(BuildCoordinator("coord", subTool: "reviewer", subAgentId: "code-reviewer"))
            .WithManifest(BuildSubAgent("code-reviewer", "Reviews code."))
            .Translator;

        var options = await translator.TranslateAsync("coord");

        options.InputMiddleware.Should().ContainSingle().Which.Should().BeOfType<CapabilityMapInputMiddleware>();
        options.ToolGatewayMiddleware.OfType<DelegationGovernanceMiddleware>().Should().BeEmpty();
    }

    [Fact]
    public async Task NoCapabilityMapBuilderRegistered_SkipsBothMiddlewares()
    {
        var translator = new TranslatorFixture()
            .WithProvider("openai")
            .WithDelegationPolicy(AllowAllDelegationPolicy.Instance)
            .WithManifest(BuildCoordinator("coord", subTool: "reviewer", subAgentId: "code-reviewer"))
            .WithManifest(BuildSubAgent("code-reviewer", "Reviews code."))
            .Translator;

        var options = await translator.TranslateAsync("coord");

        options.InputMiddleware.Should().BeEmpty("no builder registered ⇒ no auto-wiring");
        options.ToolGatewayMiddleware.OfType<DelegationGovernanceMiddleware>().Should().BeEmpty();
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static TranslatorFixture NewFixture() =>
        new TranslatorFixture()
            .WithProvider("openai")
            .WithCapabilityMapBuilder(new StubBuilder());

    private static AgentManifest BuildCoordinator(string id, string subTool, string subAgentId) =>
        new(id, "1.0",
            new AgentHandlerRef("default", "maf"),
            [new ProtocolBinding("openai")],
            Tools: [new ToolRef(subTool, Source: $"agent:{subTool}Local")])
        {
            Model = new ModelSpec("openai", "gpt-4o-mini"),
            LocalAgents = [new LocalAgentRef($"{subTool}Local", AgentId: subAgentId)],
        };

    private static AgentManifest BuildLonelyAgent(string id) =>
        new(id, "1.0",
            new AgentHandlerRef("default", "maf"),
            [new ProtocolBinding("openai")],
            Tools: [])
        {
            Model = new ModelSpec("openai", "gpt-4o-mini"),
        };

    private static AgentManifest BuildSubAgent(string id, string description) =>
        new(id, "1.0",
            new AgentHandlerRef("default", "maf"),
            [new ProtocolBinding("openai")],
            Tools: [])
        {
            Model = new ModelSpec("openai", "gpt-4o-mini"),
            Description = description,
        };

    private sealed class StubBuilder : IAgentCapabilityMapBuilder
    {
        public ValueTask<CapabilityMap> BuildAsync(string coordinatorAgentId, CancellationToken cancellationToken = default)
            => new(CapabilityMap.Empty(coordinatorAgentId));
        public void Invalidate(string coordinatorAgentId) { }
    }
}
