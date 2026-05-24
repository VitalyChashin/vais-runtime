// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Vais.Agents.Eval;
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

    // ── vais.eval + vais.eval.status (NB-11) ──────────────────────────────────

    private const string EvalSuiteJson = """
        {
          "apiVersion": "vais.agents/v1",
          "kind": "EvalSuite",
          "metadata": { "id": "s1", "version": "1.0" },
          "spec": { "agentId": "a1",
                    "cases": [ { "id": "greets", "input": "Say hello.",
                                 "assertions": [ { "kind": "contains", "params": { "value": "hello" } } ] } ] }
        }
        """;

    [Fact]
    public async Task Eval_SuiteRef_Starts_Run()
    {
        var runMgr = Substitute.For<IEvalRunLifecycleManager>();
        runMgr.StartRunAsync("s1", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<string>("run-1"));
        var sp = EvalSp(runMgr);

        var doc = await InvokeJson(sp, "vais.eval", new { suiteRef = "s1" });

        doc.GetProperty("ok").GetBoolean().Should().BeTrue();
        doc.GetProperty("runId").GetString().Should().Be("run-1");
    }

    [Fact]
    public async Task Eval_InlineSuite_Allowed_Registers_And_Runs()
    {
        var runMgr = Substitute.For<IEvalRunLifecycleManager>();
        runMgr.StartRunAsync("s1", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<string>("run-2"));
        var suiteReg = Substitute.For<IEvalSuiteRegistry>();
        var sp = EvalSp(runMgr, suiteReg);

        var doc = await InvokeJson(sp, "vais.eval", new { suite = EvalSuiteJson });

        doc.GetProperty("ok").GetBoolean().Should().BeTrue();
        doc.GetProperty("runId").GetString().Should().Be("run-2");
        await suiteReg.Received(1).UpsertAsync(Arg.Is<EvalSuiteManifest>(s => s.Id == "s1"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Eval_InlineSuite_Denied_DoesNotRegisterOrRun()
    {
        var runMgr = Substitute.For<IEvalRunLifecycleManager>();
        var suiteReg = Substitute.For<IEvalSuiteRegistry>();
        var sp = EvalSp(runMgr, suiteReg, new DenyAllPolicy());

        var doc = await InvokeJson(sp, "vais.eval", new { suite = EvalSuiteJson });

        doc.GetProperty("ok").GetBoolean().Should().BeFalse();
        doc.GetProperty("denied").GetBoolean().Should().BeTrue();
        await suiteReg.DidNotReceive().UpsertAsync(Arg.Any<EvalSuiteManifest>(), Arg.Any<CancellationToken>());
        await runMgr.DidNotReceive().StartRunAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvalStatus_Returns_Summary()
    {
        var runMgr = Substitute.For<IEvalRunLifecycleManager>();
        var detail = new EvalRunDetail(
            new EvalRunSummary("run-1", "s1", "1.0", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, EvalRunStatus.Completed, 2, 2, 0),
            []);
        runMgr.GetRunDetailAsync("run-1", Arg.Any<CancellationToken>()).Returns(new ValueTask<EvalRunDetail?>(detail));
        var sp = EvalSp(runMgr);

        var doc = await InvokeJson(sp, "vais.eval.status", new { runId = "run-1" });

        doc.GetProperty("ok").GetBoolean().Should().BeTrue();
        doc.GetProperty("status").GetString().Should().Be("Completed");
        doc.GetProperty("passedCases").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task EvalStatus_Unknown_Returns_Error()
    {
        var runMgr = Substitute.For<IEvalRunLifecycleManager>();
        runMgr.GetRunDetailAsync("ghost", Arg.Any<CancellationToken>()).Returns(new ValueTask<EvalRunDetail?>((EvalRunDetail?)null));
        var sp = EvalSp(runMgr);

        var doc = await InvokeJson(sp, "vais.eval.status", new { runId = "ghost" });

        doc.GetProperty("ok").GetBoolean().Should().BeFalse();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IServiceProvider Sp(IAgentRegistry registry, IAgentLifecycleManager manager)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton(registry);
        sc.AddSingleton(manager);
        return sc.BuildServiceProvider();
    }

    private static IServiceProvider EvalSp(IEvalRunLifecycleManager runMgr, IEvalSuiteRegistry? suiteReg = null, IAgentPolicyEngine? policy = null)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton(runMgr);
        sc.AddSingleton(suiteReg ?? Substitute.For<IEvalSuiteRegistry>());
        if (policy is not null) sc.AddSingleton(policy);
        return sc.BuildServiceProvider();
    }

    private static async Task<JsonElement> InvokeJson(IServiceProvider sp, string tool, object argsObj)
    {
        var argsJson = JsonSerializer.Serialize(argsObj);
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson)!;
        var result = await DesignMcpToolHandlers.InvokeAsync(tool, args, sp, default);
        var text = ((TextContentBlock)result.Content[0]).Text;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private sealed class DenyAllPolicy : IAgentPolicyEngine
    {
        public ValueTask<PolicyDecision> EvaluateAsync(PolicyOperation operation, AgentManifest? manifest, AgentPrincipal? principal, CancellationToken cancellationToken = default)
            => new(PolicyDecision.Deny("read-only"));
    }
}
