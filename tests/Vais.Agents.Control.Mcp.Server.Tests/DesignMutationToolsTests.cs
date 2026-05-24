// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Vais.Agents.Control.Mcp.Server.Tests;

/// <summary>
/// NB-9 / NB-10 / NB-12 — the MCP mutating verbs route create-or-update + delete
/// through the lifecycle managers, and tools/list hides mutating verbs from a caller
/// that can author nothing.
/// </summary>
public sealed class DesignMutationToolsTests
{
    private const string AgentManifestJson = """
        {
          "apiVersion": "vais.agents/v1",
          "kind": "Agent",
          "metadata": { "id": "a1", "version": "1.0" },
          "spec": { "handler": { "name": "maf" }, "protocols": [ { "kind": "openai" } ] }
        }
        """;

    private static AgentManifest ExistingAgent() =>
        new("a1", "1.0", new AgentHandlerRef("maf"), [new ProtocolBinding("openai")], []);

    // ── vais.apply routing (NB-9) ─────────────────────────────────────────────

    [Fact]
    public async Task Apply_Agent_Creates_When_Absent()
    {
        var registry = Substitute.For<IAgentRegistry>();
        registry.GetAsync("a1", "1.0", Arg.Any<CancellationToken>()).Returns(new ValueTask<AgentManifest?>((AgentManifest?)null));
        var manager = Substitute.For<IAgentLifecycleManager>();
        manager.CreateAsync(Arg.Any<AgentManifest>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<AgentHandle>(new AgentHandle("a1", "1.0")));
        var sp = Sp(registry, manager);

        var result = await DesignMutationRouter.ApplyAsync(AgentManifestJson, sp, default);

        result["ok"]!.GetValue<bool>().Should().BeTrue();
        result["action"]!.GetValue<string>().Should().Be("created");
        result["kind"]!.GetValue<string>().Should().Be("Agent");
        await manager.Received(1).CreateAsync(Arg.Any<AgentManifest>(), Arg.Any<CancellationToken>());
        await manager.DidNotReceive().UpdateAsync(Arg.Any<AgentHandle>(), Arg.Any<AgentManifest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_Agent_Updates_When_Present()
    {
        var registry = Substitute.For<IAgentRegistry>();
        registry.GetAsync("a1", "1.0", Arg.Any<CancellationToken>()).Returns(new ValueTask<AgentManifest?>(ExistingAgent()));
        var manager = Substitute.For<IAgentLifecycleManager>();
        manager.UpdateAsync(Arg.Any<AgentHandle>(), Arg.Any<AgentManifest>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<AgentHandle>(new AgentHandle("a1", "1.0")));
        var sp = Sp(registry, manager);

        var result = await DesignMutationRouter.ApplyAsync(AgentManifestJson, sp, default);

        result["action"]!.GetValue<string>().Should().Be("updated");
        await manager.Received(1).UpdateAsync(Arg.Any<AgentHandle>(), Arg.Any<AgentManifest>(), Arg.Any<CancellationToken>());
        await manager.DidNotReceive().CreateAsync(Arg.Any<AgentManifest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_Unknown_Kind_Is_Not_Applyable()
    {
        // A kind with no lifecycle manager reaches the switch default → not applyable.
        const string unknown = """
            { "apiVersion": "vais.agents/v1", "kind": "Banana", "metadata": { "id": "b1", "version": "1.0" }, "spec": {} }
            """;
        var result = await DesignMutationRouter.ApplyAsync(unknown, Sp(Substitute.For<IAgentRegistry>(), Substitute.For<IAgentLifecycleManager>()), default);

        result["ok"]!.GetValue<bool>().Should().BeFalse();
    }

    // ── vais.delete routing (NB-10) ───────────────────────────────────────────

    [Fact]
    public async Task Delete_Agent_Evicts_When_Present()
    {
        var registry = Substitute.For<IAgentRegistry>();
        registry.GetAsync("a1", null, Arg.Any<CancellationToken>()).Returns(new ValueTask<AgentManifest?>(ExistingAgent()));
        var manager = Substitute.For<IAgentLifecycleManager>();
        var sp = Sp(registry, manager);

        var result = await DesignMutationRouter.DeleteAsync("Agent", "a1", null, sp, default);

        result["action"]!.GetValue<string>().Should().Be("deleted");
        await manager.Received(1).EvictAsync(Arg.Is<AgentHandle>(h => h.AgentId == "a1"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_Agent_NotFound_Returns_Error()
    {
        var registry = Substitute.For<IAgentRegistry>();
        registry.GetAsync("ghost", null, Arg.Any<CancellationToken>()).Returns(new ValueTask<AgentManifest?>((AgentManifest?)null));
        var sp = Sp(registry, Substitute.For<IAgentLifecycleManager>());

        var result = await DesignMutationRouter.DeleteAsync("Agent", "ghost", null, sp, default);

        result["ok"]!.GetValue<bool>().Should().BeFalse();
        result["error"]!.GetValue<string>().Should().Contain("not registered");
    }

    // ── tools/list scope filter (NB-12) ───────────────────────────────────────

    [Fact]
    public async Task ToolsList_Includes_Mutating_Verbs_When_Allowed()
    {
        // No policy registered → NullAgentPolicyEngine allows → mutating verbs shown (dev posture).
        var result = await DesignMcpToolHandlers.ListToolsAsync(new ServiceCollection().BuildServiceProvider(), default);
        result.Tools.Select(t => t.Name).Should().Contain(["vais.apply", "vais.delete"]);
    }

    [Fact]
    public async Task ToolsList_Hides_Mutating_Verbs_For_ReadOnly_Caller()
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<IAgentPolicyEngine>(new DenyAllPolicy());
        var result = await DesignMcpToolHandlers.ListToolsAsync(sc.BuildServiceProvider(), default);

        var names = result.Tools.Select(t => t.Name).ToList();
        names.Should().NotContain("vais.apply");
        names.Should().NotContain("vais.delete");
        names.Should().Contain("vais.list"); // read verbs still present
    }

    private static IServiceProvider Sp(IAgentRegistry registry, IAgentLifecycleManager manager)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton(registry);
        sc.AddSingleton(manager);
        return sc.BuildServiceProvider();
    }

    private sealed class DenyAllPolicy : IAgentPolicyEngine
    {
        public ValueTask<PolicyDecision> EvaluateAsync(PolicyOperation operation, AgentManifest? manifest, AgentPrincipal? principal, CancellationToken cancellationToken = default)
            => new(PolicyDecision.Deny("read-only"));
    }
}
