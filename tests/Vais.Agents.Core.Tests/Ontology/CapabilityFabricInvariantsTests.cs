// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Reflection;
using FluentAssertions;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Core.Tests.Ontology;

/// <summary>
/// C2-6 + C2-7 guard tests for the capability fabric.
/// <list type="bullet">
///   <item><description><b>C2-6 composition + isolation:</b> coordinator-level governance and a sub-agent's own C1 cartridge live on separate per-agent middleware chains; neither bleeds into the other (existing per-agent translation isolation is the structural guarantee, asserted here over the cartridge instances).</description></item>
///   <item><description><b>C2-7 no-sequencing (§14.5):</b> ontology recipes / capability maps are surfaced + validated; they MUST NOT auto-execute control flow. The capability-fabric components (CapabilityMap text rendering, CapabilityMapInputMiddleware) do not invoke tools / runtimes / agents under any input. No public type in <c>Vais.Agents.Control.Manifests.Json</c> matches a sequencer / auto-executor pattern.</description></item>
/// </list>
/// </summary>
public sealed class CapabilityFabricInvariantsTests
{
    // ── C2-6 composition + isolation ──────────────────────────────────────────

    [Fact]
    public void TwoCoordinatorsCanCarryIndependentDelegationMiddlewareInstances()
    {
        // Composition contract: each coordinator builds its own ToolGatewayMiddleware
        // chain. Two coordinators each get a fresh DelegationGovernanceMiddleware
        // instance from their own DI scope — no shared mutable state.
        var policyA = new FixedPolicy(DelegationDecision.Deny("A-only-policy"));
        var policyB = new FixedPolicy(DelegationDecision.Allow);
        var builder = new StaticMapBuilder(new CapabilityMap("any", []));

        var mwA = new DelegationGovernanceMiddleware(policyA, builder);
        var mwB = new DelegationGovernanceMiddleware(policyB, builder);

        mwA.Should().NotBeSameAs(mwB);
        ((OntologyInterceptor)mwA).Kind.Should().Be(InterceptorKind.Validation);
        ((OntologyInterceptor)mwB).Kind.Should().Be(InterceptorKind.Validation);
    }

    [Fact]
    public void CoordinatorAndSubAgentCartridgesComposeIndependently()
    {
        // The coordinator's DelegationGovernanceMiddleware (Plan C2) and a sub-agent's south
        // arg-validation middleware (Plan C1) are independent ToolGatewayMiddleware
        // instances — each lives on its own per-agent chain, neither carries shared state.
        var subCatalog = new DomainOntologyCatalog(new DomainOntologyArtifact { OntologyVersion = "v1" });
        var subValidator = new DomainOntologyArgValidationMiddleware(subCatalog);

        var coordPolicy = new FixedPolicy(DelegationDecision.Allow);
        var coordBuilder = new StaticMapBuilder(new CapabilityMap("coord", [
            new SubAgentCapability("worker", "worker-bot", "Worker.", [], LocalAgentInvocationMode.Blocking),
        ]));
        var coordGov = new DelegationGovernanceMiddleware(coordPolicy, coordBuilder);

        // Two distinct middleware instances on two distinct chains; the substrate's metadata
        // discriminator confirms each interceptor declares its own Kind.
        coordGov.Should().NotBe(subValidator);
        ((OntologyInterceptor)coordGov).Kind.Should().Be(InterceptorKind.Validation);
        ((OntologyInterceptor)subValidator).Kind.Should().Be(InterceptorKind.Validation);

        // Each cartridge slot is independently constructible — no shared singleton smuggled in.
        var coordGov2 = new DelegationGovernanceMiddleware(coordPolicy, coordBuilder);
        coordGov2.Should().NotBeSameAs(coordGov, "constructor returns a fresh instance per agent");
    }

    // ── C2-7 no-sequencing guard (§14.5) ──────────────────────────────────────

    [Fact]
    public async Task CapabilityMapInputMiddleware_NeverInvokesAToolOrAgent_WhateverTheMapContents()
    {
        // The capability fabric stays advisory: it SHAPES the agent's prompt and surfaces a
        // structured map; it MUST NOT call any tool, agent, or runtime as a side effect of
        // map presence — even when the map mentions sub-agents that could be sequenced.
        var map = new CapabilityMap("coord", [
            new SubAgentCapability("reviewer", "reviewer-bot", "Reviews code.",   ["role:review"], LocalAgentInvocationMode.Blocking),
            new SubAgentCapability("deployer", "deploy-bot",   "Deploys to dev.", ["role:ops"],     LocalAgentInvocationMode.Blocking),
        ]);
        var mw = new CapabilityMapInputMiddleware(new StaticMapBuilder(map));
        var ctx = new AgentInputContext { AgentId = "coord", Message = "go" };

        await mw.InvokeAsync(ctx, () => Task.CompletedTask);

        // After the middleware runs, the only observable effect must be the in-band text +
        // the structured Properties entry. Crucially, this test runs without ANY IAgentRuntime,
        // ITool, or ToolGatewayMiddleware registered — any auto-execution attempt would have
        // thrown NRE / ServiceNotRegistered. The fact that we reach this assertion proves
        // the fabric is advisory-only by construction.
        ctx.Properties.Should().ContainKey(CapabilityMapInputMiddleware.ContextPropertyKey);
        ctx.Message.Should().Contain("reviewer").And.Contain("deployer");
    }

    [Fact]
    public void Manifests_AssemblyExposesNoSequencerOrAutoExecutorType()
    {
        // Structural pin: §14.5 prohibits the ontology from sequencing. To keep this honest
        // across refactors, the assembly that holds the capability fabric must not surface
        // any public type whose name suggests automatic delegation orchestration. New
        // additions matching these patterns should be challenged or moved into the Graphs
        // pillar (P7) instead.
        var asm = typeof(CapabilityMap).Assembly;
        var forbiddenSubstrings = new[] { "Sequencer", "AutoExecutor", "RecipeExecutor", "AutoDelegator" };

        var offenders = asm.GetExportedTypes()
            .Where(t => forbiddenSubstrings.Any(s => t.Name.Contains(s, StringComparison.Ordinal)))
            .Select(t => t.FullName)
            .ToList();

        offenders.Should().BeEmpty(
            "the capability fabric must stay advisory — sequencing belongs to Graphs (P7), " +
            "not the ontology layer (§14.5). Found: " + string.Join(", ", offenders));
    }

    [Fact]
    public void RecipeEntries_FromOntologyOverlay_AreSurfaceableButDoNotImplementITool()
    {
        // OntologyOverlay.RecipeEntry exists as authoring guidance. It MUST NOT implement
        // ITool / ToolGatewayMiddleware / IAgentHandlerFactory — those would let recipes
        // self-execute. This test pins the type's role to "data carrier only".
        var recipeType = typeof(RecipeEntry);

        recipeType.GetInterfaces().Should().NotContain(
            t => t.Namespace == "Vais.Agents"
                 && (t.Name == "ITool" || t.Name.Contains("Middleware", StringComparison.Ordinal)
                                       || t.Name.Contains("HandlerFactory", StringComparison.Ordinal)),
            "RecipeEntry is advisory data — implementing an execution interface would violate §14.5");

        // It must not have public methods that smell like execution either.
        var executionMethods = recipeType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.DeclaringType == recipeType)
            .Select(m => m.Name)
            .Where(n => n.StartsWith("Execute", StringComparison.Ordinal)
                        || n.StartsWith("Run", StringComparison.Ordinal)
                        || n.StartsWith("Invoke", StringComparison.Ordinal))
            .ToList();
        executionMethods.Should().BeEmpty(
            "RecipeEntry must expose only data (no Execute/Run/Invoke methods). Found: " + string.Join(", ", executionMethods));
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private sealed class FixedPolicy(DelegationDecision decision) : IDelegationPolicy
    {
        public ValueTask<DelegationDecision> EvaluateAsync(
            ToolGatewayContext context, CapabilityMap map, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(decision);
    }

    private sealed class StaticMapBuilder(CapabilityMap map) : IAgentCapabilityMapBuilder
    {
        public ValueTask<CapabilityMap> BuildAsync(string coordinatorAgentId, CancellationToken cancellationToken = default)
            => new(map);
        public void Invalidate(string coordinatorAgentId) { }
    }
}
