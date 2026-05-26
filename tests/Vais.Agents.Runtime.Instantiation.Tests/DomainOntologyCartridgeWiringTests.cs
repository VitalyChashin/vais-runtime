// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using NSubstitute;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Runtime.Instantiation.Tests;

/// <summary>
/// Plan C1 follow-on wiring (FU-1/FU-2): when a manifest references a virtual MCP server
/// whose <see cref="McpServerManifest.OntologyRef"/> resolves through
/// <see cref="IDomainOntologyArtifactRegistry"/>, the translator must (a) append
/// <see cref="DomainOntologyArgValidationMiddleware"/> + <see cref="DomainOntologyResponseEnrichmentMiddleware"/>
/// to the per-agent tool-gateway chain and (b) install a shape callback on the
/// <see cref="VirtualMcpToolSource"/> so projected tools get description rewrites + hide-tag
/// filtering. Unknown refs degrade gracefully (no cartridge appended).
/// </summary>
public sealed class DomainOntologyCartridgeWiringTests
{
    private const string AgentId = "ontology-agent";
    private const string VirtualServerId = "k8s-virtual";

    [Fact]
    public async Task Cartridge_AppendsCallTimeMiddlewaresWhenOntologyRefResolves()
    {
        var registry = new InMemoryDomainOntologyArtifactRegistry();
        registry.Register("k8s-tools-v1", new DomainOntologyArtifact
        {
            OntologyVersion = "v1",
            Tools = new Dictionary<string, DomainConcept>
            {
                ["kubectl_get"] = new() { Tags = ["risk:read"] },
            },
        });

        var translator = NewTranslator(registry, ontologyRef: "k8s-tools-v1");

        var options = await translator.TranslateAsync(AgentId);

        var names = options.ToolGatewayMiddleware?.Select(m => m.GetType().Name).ToList()
                    ?? new List<string>();
        names.Should().Contain(nameof(DomainOntologyArgValidationMiddleware));
        names.Should().Contain(nameof(DomainOntologyResponseEnrichmentMiddleware));
    }

    [Fact]
    public async Task Cartridge_SkipsAppendWhenOntologyRefIsAbsent()
    {
        var registry = new InMemoryDomainOntologyArtifactRegistry();
        var translator = NewTranslator(registry, ontologyRef: null);

        var options = await translator.TranslateAsync(AgentId);

        (options.ToolGatewayMiddleware ?? Array.Empty<ToolGatewayMiddleware>())
            .Select(m => m.GetType().Name)
            .Should().NotContain([
                nameof(DomainOntologyArgValidationMiddleware),
                nameof(DomainOntologyResponseEnrichmentMiddleware)
            ]);
    }

    [Fact]
    public async Task Cartridge_SkipsAppendWhenOntologyRefDoesNotResolve()
    {
        var registry = new InMemoryDomainOntologyArtifactRegistry(); // empty — unknown ref
        var translator = NewTranslator(registry, ontologyRef: "missing-ontology");

        var options = await translator.TranslateAsync(AgentId);

        (options.ToolGatewayMiddleware ?? Array.Empty<ToolGatewayMiddleware>())
            .Select(m => m.GetType().Name)
            .Should().NotContain([
                nameof(DomainOntologyArgValidationMiddleware),
                nameof(DomainOntologyResponseEnrichmentMiddleware)
            ], "unknown OntologyRef must degrade gracefully (no cartridge appended)");
    }

    [Fact]
    public async Task Cartridge_AppendsBesidesAnyExistingMcpGatewayChain()
    {
        // When McpGatewayRef pre-populates the chain with e.g. ToolLogging + ToolDeny,
        // the cartridge middlewares append AFTER (innermost in the right-to-left composition).
        var registry = new InMemoryDomainOntologyArtifactRegistry();
        registry.Register("k8s-tools-v1", new DomainOntologyArtifact { OntologyVersion = "v1" });

        var translator = NewTranslator(registry, ontologyRef: "k8s-tools-v1");
        var options = await translator.TranslateAsync(AgentId);

        var chain = (options.ToolGatewayMiddleware ?? Array.Empty<ToolGatewayMiddleware>()).ToList();
        chain.Count.Should().BeGreaterThanOrEqualTo(2);
        chain[^2].Should().BeOfType<DomainOntologyArgValidationMiddleware>();
        chain[^1].Should().BeOfType<DomainOntologyResponseEnrichmentMiddleware>();
    }

    // ── fixture ────────────────────────────────────────────────────────────────

    private static IAgentManifestTranslator NewTranslator(
        IDomainOntologyArtifactRegistry registry, string? ontologyRef)
    {
        var virtualServer = new McpServerManifest(VirtualServerId, "1.0")
        {
            Virtual = true,
            Sources = [new McpServerSourceRef("dummy-upstream")],
            OntologyRef = ontologyRef,
        };

        var serverRegistry = Substitute.For<IMcpServerRegistry>();
        serverRegistry.GetAsync(VirtualServerId, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpServerManifest?>(virtualServer));

        var manifest = new AgentManifest(
            AgentId,
            "1.0",
            new AgentHandlerRef("maf"),
            [new ProtocolBinding("openai")],
            Tools: [])
        {
            Model = new ModelSpec("openai", "gpt-4o-mini"),
            McpServers = [new McpServerRef(VirtualServerId, McpServerRef.RegisteredTransport)],
        };

        var fixture = new TranslatorFixture()
            .WithProvider("openai")
            .WithMcpServerRegistry(serverRegistry)
            .WithToolSourceProvider(new FakeNamedToolSourceProvider("dummy-upstream"))
            .WithDomainOntologyRegistry(registry)
            .WithManifest(manifest);
        return fixture.Translator;
    }

    private sealed class FakeNamedToolSourceProvider(string name) : INamedToolSourceProvider
    {
        public IToolSource? GetByName(string serverName)
            => string.Equals(serverName, name, StringComparison.Ordinal)
                ? new EmptyToolSource()
                : null;

        private sealed class EmptyToolSource : IToolSource
        {
            public async IAsyncEnumerable<ITool> DiscoverAsync(
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.CompletedTask;
                yield break;
            }
        }
    }
}
