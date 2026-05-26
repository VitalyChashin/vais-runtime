// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Vais.Agents.Control.Manifests;
using Vais.Agents.Control.Mcp.Server.Ontology;
using Xunit;

namespace Vais.Agents.Control.Mcp.Server.Tests;

/// <summary>
/// Phase 2 substrate-seam tests for the north read-roles. Byte-parity with the inline path
/// is already covered by <c>DesignMutationToolsTests.ToolsList_*</c> and the
/// <c>DesignMcpToolsTests.VaisValidate_*</c> tests; this file proves the seam itself works.
/// </summary>
public sealed class DesignOntologyInterceptorTests
{
    // ── C1-5: list-role substrate seam ─────────────────────────────────────────

    [Fact]
    public void ScopeFilterInterceptor_IsMutationKind()
    {
        var filter = new DesignToolsScopeFilterInterceptor();
        ((OntologyInterceptor)filter).Kind.Should().Be(InterceptorKind.Mutation);
    }

    [Fact]
    public async Task ListToolsAsync_DeployerInterceptorCanAppendCustomTool()
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<OntologyInterceptor<DesignToolsListInterceptionContext, ListToolsResult>>(
            new AppendCustomToolInterceptor("vais.deployer.ping"));

        var result = await DesignMcpToolHandlers.ListToolsAsync(sc.BuildServiceProvider(), default);

        result.Tools.Select(t => t.Name).Should().Contain("vais.deployer.ping",
            "the deployer-registered interceptor must layer around the built-in scope filter");
        result.Tools.Select(t => t.Name).Should().Contain("vais.list",
            "the read-only baseline must still be present");
    }

    [Fact]
    public async Task ListToolsAsync_DeployerInterceptorRunsAroundBuiltInScopeFilter()
    {
        // DenyAllPolicy hides mutating verbs; deployer interceptor adds a custom tool.
        // Result: read-only baseline + deployer's tool, no mutating verbs.
        var sc = new ServiceCollection();
        sc.AddSingleton<IAgentPolicyEngine>(new DenyAllPolicy());
        sc.AddSingleton<OntologyInterceptor<DesignToolsListInterceptionContext, ListToolsResult>>(
            new AppendCustomToolInterceptor("vais.deployer.ping"));

        var result = await DesignMcpToolHandlers.ListToolsAsync(sc.BuildServiceProvider(), default);

        var names = result.Tools.Select(t => t.Name).ToList();
        names.Should().Contain("vais.list");
        names.Should().Contain("vais.deployer.ping");
        names.Should().NotContain("vais.apply", "DenyAllPolicy hides mutating verbs at the built-in scope filter layer");
    }

    // ── C1-6: validate-role substrate seam ─────────────────────────────────────

    [Fact]
    public void ManifestValidatorInterceptor_IsValidationKind()
    {
        var validator = new ManifestValidatorInterceptor(new ServiceCollection().BuildServiceProvider());
        ((OntologyInterceptor)validator).Kind.Should().Be(InterceptorKind.Validation);
    }

    [Fact]
    public async Task ValidateChain_DeployerInterceptorCanAddErrors()
    {
        var sp = BuildValidateSp(extra: sc =>
            sc.AddSingleton<OntologyInterceptor<DesignValidateInterceptionContext, ValidationOutcome>>(
                new AddErrorInterceptor("deployer-policy-violation")));

        var outcome = await DesignMcpToolHandlers.RunValidationChainAsync(ValidAgentManifest, sp, default);

        outcome.Ok.Should().BeFalse("the deployer interceptor injected an error");
        outcome.Errors.Should().Contain("deployer-policy-violation");
    }

    [Fact]
    public async Task ValidateChain_ProducesByteIdenticalOutcomeToInlineValidator()
    {
        // With no deployer interceptors registered, the substrate chain output must match
        // ManifestValidator.ValidateAsync exactly.
        var sp = BuildValidateSp();

        var inline = await ManifestValidator.ValidateAsync(ValidAgentManifest, sp, default);
        var viaChain = await DesignMcpToolHandlers.RunValidationChainAsync(ValidAgentManifest, sp, default);

        viaChain.Ok.Should().Be(inline.Ok);
        viaChain.Errors.Should().BeEquivalentTo(inline.Errors);
        viaChain.Suggestions.Should().BeEquivalentTo(inline.Suggestions);
    }

    [Fact]
    public async Task ValidateChain_OnlyInvokesReadOnlyRegistryMethods()
    {
        // The IAgentRegistry interface is read-only by design (ListAsync + GetAsync only) — there
        // are no write methods for the validator to call. This test operationally documents the
        // no-mutation property: only GetAsync is invoked on the agent registry while validating
        // a manifest whose cross-ref (localAgents[].agentId → Agent) forces a registry lookup.
        var agentRegistry = Substitute.For<IAgentRegistry>();
        agentRegistry.GetAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<AgentManifest?>((AgentManifest?)null));

        var sp = BuildValidateSp(extra: sc => sc.Replace(ServiceDescriptor.Singleton(agentRegistry)));
        const string manifestWithCrossRef = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "Agent",
              "metadata": { "id": "parent-agent", "version": "1.0" },
              "spec": {
                "handler": { "kind": "maf" },
                "protocols": [ { "name": "openai" } ],
                "localAgents": [ { "agentId": "unresolved-child" } ]
              }
            }
            """;

        await DesignMcpToolHandlers.RunValidationChainAsync(manifestWithCrossRef, sp, default);

        var calls = agentRegistry.ReceivedCalls().Select(c => c.GetMethodInfo().Name).Distinct().ToList();
        calls.Should().NotBeEmpty("the cross-ref must have triggered a registry lookup");
        calls.Should().OnlyContain(name => name == nameof(IAgentRegistry.GetAsync) || name == nameof(IAgentRegistry.ListAsync),
            "validation must be read-only — observed method calls: " + string.Join(", ", calls));
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private const string ValidAgentManifest = """
        {
          "apiVersion": "vais.agents/v1",
          "kind": "Agent",
          "metadata": { "id": "test-agent", "version": "1.0" },
          "spec": { "handler": { "kind": "maf" }, "protocols": [ { "name": "openai" } ] }
        }
        """;

    private static IServiceProvider BuildValidateSp(Action<IServiceCollection>? extra = null)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<IOntologyCatalog>(_ => OntologyCatalog.BuildFromEmbeddedBase());
        sc.AddSingleton<IAgentRegistry>(Substitute.For<IAgentRegistry>());
        sc.AddSingleton<IAgentGraphRegistry>(Substitute.For<IAgentGraphRegistry>());
        sc.AddSingleton<IMcpServerRegistry>(Substitute.For<IMcpServerRegistry>());
        sc.AddSingleton<ILlmGatewayConfigRegistry>(Substitute.For<ILlmGatewayConfigRegistry>());
        sc.AddSingleton<IMcpGatewayConfigRegistry>(Substitute.For<IMcpGatewayConfigRegistry>());
        sc.AddSingleton<IContainerPluginRegistry>(Substitute.For<IContainerPluginRegistry>());
        sc.AddSingleton<IEvalSuiteRegistry>(Substitute.For<IEvalSuiteRegistry>());
        extra?.Invoke(sc);
        return sc.BuildServiceProvider();
    }

    // ── test interceptors ──────────────────────────────────────────────────────

    private sealed class AppendCustomToolInterceptor(string toolName)
        : OntologyInterceptor<DesignToolsListInterceptionContext, ListToolsResult>
    {
        public override async Task<ListToolsResult> InvokeAsync(
            DesignToolsListInterceptionContext context,
            Func<Task<ListToolsResult>> next,
            CancellationToken cancellationToken = default)
        {
            var inner = await next().ConfigureAwait(false);
            var combined = new List<Tool>(inner.Tools) { new() { Name = toolName, Description = "deployer" } };
            return new ListToolsResult { Tools = combined };
        }
    }

    private sealed class AddErrorInterceptor(string error)
        : OntologyInterceptor<DesignValidateInterceptionContext, ValidationOutcome>
    {
        public override async Task<ValidationOutcome> InvokeAsync(
            DesignValidateInterceptionContext context,
            Func<Task<ValidationOutcome>> next,
            CancellationToken cancellationToken = default)
        {
            var inner = await next().ConfigureAwait(false);
            var errors = new List<string>(inner.Errors) { error };
            return new ValidationOutcome(false, errors, inner.Suggestions);
        }
    }

    private sealed class DenyAllPolicy : IAgentPolicyEngine
    {
        public ValueTask<PolicyDecision> EvaluateAsync(
            PolicyOperation operation, AgentManifest? manifest, AgentPrincipal? principal,
            CancellationToken cancellationToken = default)
            => new(PolicyDecision.Deny("read-only"));
    }
}
