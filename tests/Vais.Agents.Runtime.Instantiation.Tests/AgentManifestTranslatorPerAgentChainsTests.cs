// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using FluentAssertions;
using NSubstitute;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Runtime.Instantiation.Tests;

/// <summary>
/// PAM-8: covers the new <see cref="IAgentManifestTranslator.ResolvePerAgentChainsAsync"/>
/// surface and verifies the PAM-7 plugin-branch fix — that <c>StatefulAgentOptions</c> for
/// plugin agents now carries the per-agent middleware chains, not just DI-global. The plugin
/// scenarios at the bottom of this file (LlmGatewayRef + OntologyRef south cartridge) fail
/// against the pre-PAM-7 translator and pass after; they are the regression guard for the
/// container-plugin per-agent middleware bypass gap.
/// </summary>
public class AgentManifestTranslatorPerAgentChainsTests
{
    private const string AgentId = "agent-pam";
    private const string Version = "1.0";
    private const string PluginHandler = "Vais.Agents.Samples.PluginAgent";

    // ── ResolvePerAgentChainsAsync — declarative manifests ─────────────────────

    [Fact]
    public async Task ResolvePerAgentChainsAsync_LlmGatewayRef_ReturnsConfiguredChain()
    {
        var expected = new FakeLlmMiddleware();
        var (registry, factory) = BuildLlmGatewayConfig("llm-gw", expected);

        var manifest = BuildManifest(llmGatewayRef: "llm-gw");
        var fixture = new TranslatorFixture()
            .WithManifest(manifest).WithProvider("openai")
            .WithLlmGatewayConfigRegistry(registry)
            .WithLlmGatewayFactory(factory);

        var chains = await fixture.Translator.ResolvePerAgentChainsAsync(AgentId);

        chains.Llm.Should().ContainSingle().Which.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task ResolvePerAgentChainsAsync_NoLlmGatewayRef_FallsBackToDiGlobal()
    {
        var globalMw = new FakeLlmMiddleware();
        var manifest = BuildManifest();

        var fixture = new TranslatorFixture()
            .WithManifest(manifest).WithProvider("openai")
            .WithDiGlobalLlmMiddleware(globalMw);

        var chains = await fixture.Translator.ResolvePerAgentChainsAsync(AgentId);

        chains.Llm.Should().ContainSingle().Which.Should().BeSameAs(globalMw);
    }

    [Fact]
    public async Task ResolvePerAgentChainsAsync_McpGatewayRef_ReturnsConfiguredChain()
    {
        var expected = new FakeToolMiddleware();
        var (registry, factory) = BuildMcpGatewayConfig("mcp-gw", expected);

        var manifest = BuildManifest(mcpGatewayRef: "mcp-gw");
        var fixture = new TranslatorFixture()
            .WithManifest(manifest).WithProvider("openai")
            .WithMcpGatewayConfigRegistry(registry)
            .WithToolGatewayFactory(factory);

        var chains = await fixture.Translator.ResolvePerAgentChainsAsync(AgentId);

        chains.Tool.Should().ContainSingle().Which.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task ResolvePerAgentChainsAsync_OntologyRef_AppendsSouthCartridge()
    {
        // Plan C1 south cartridge: when an MCP server carries an OntologyRef that resolves,
        // the per-agent tool chain ends with the arg-validation + response-enrichment middlewares.
        // (Trace middleware is conditional on IInterceptorTee being registered — covered by
        // DomainOntologyCartridgeWiringTests via the TranslateAsync path; here we just assert
        // the cartridge tail is present in the resolver output.)
        const string virtualServerId = "k8s-virtual";
        var ontologyRegistry = new InMemoryDomainOntologyArtifactRegistry();
        ontologyRegistry.Register("k8s-tools-v1", new DomainOntologyArtifact { OntologyVersion = "v1" });

        var virtualServer = new McpServerManifest(virtualServerId, "1.0")
        {
            Virtual = true,
            Sources = [new McpServerSourceRef("dummy-upstream")],
            OntologyRef = "k8s-tools-v1",
        };
        var serverRegistry = Substitute.For<IMcpServerRegistry>();
        serverRegistry.GetAsync(virtualServerId, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpServerManifest?>(virtualServer));

        var manifest = BuildManifest(
            mcpServers: [new McpServerRef(virtualServerId, McpServerRef.RegisteredTransport)]);

        var fixture = new TranslatorFixture()
            .WithManifest(manifest).WithProvider("openai")
            .WithMcpServerRegistry(serverRegistry)
            .WithToolSourceProvider(new EmptyNamedToolSourceProvider("dummy-upstream"))
            .WithDomainOntologyRegistry(ontologyRegistry);

        var chains = await fixture.Translator.ResolvePerAgentChainsAsync(AgentId);

        var tail = chains.Tool.TakeLast(2).Select(m => m.GetType().Name).ToList();
        tail.Should().Equal(
            nameof(DomainOntologyArgValidationMiddleware),
            nameof(DomainOntologyResponseEnrichmentMiddleware));
    }

    [Fact]
    public async Task ResolvePerAgentChainsAsync_LocalAgentsWithDelegationPolicy_AppendsC2GovernanceAndInput()
    {
        // Plan C2 capability fabric: coordinator with LocalAgents + an IDelegationPolicy →
        // tool chain has DelegationGovernanceMiddleware appended; input chain has
        // CapabilityMapInputMiddleware. With no IDelegationPolicy only the input middleware fires.
        var capabilityBuilder = Substitute.For<IAgentCapabilityMapBuilder>();
        var delegationPolicy = Substitute.For<IDelegationPolicy>();

        var manifest = new AgentManifest(
            Id: AgentId, Version: Version,
            Handler: new AgentHandlerRef("declarative"),
            Protocols: [],
            Tools: [])
        {
            Model = new ModelSpec("openai", "gpt-4o"),
            LocalAgents = [new LocalAgentRef("teammate")],
        };

        var fixture = new TranslatorFixture()
            .WithManifest(manifest).WithProvider("openai")
            .WithCapabilityMapBuilder(capabilityBuilder)
            .WithDelegationPolicy(delegationPolicy);

        var chains = await fixture.Translator.ResolvePerAgentChainsAsync(AgentId);

        chains.Input.Should().ContainSingle()
            .Which.Should().BeOfType<CapabilityMapInputMiddleware>();
        chains.Tool.Should().ContainSingle()
            .Which.Should().BeOfType<DelegationGovernanceMiddleware>();
    }

    // ── ResolvePerAgentChainsAsync — error + cache behavior ────────────────────

    [Fact]
    public async Task ResolvePerAgentChainsAsync_UnknownAgent_ThrowsAgentNotFound()
    {
        var fixture = new TranslatorFixture().WithProvider("openai");

        var act = async () => await fixture.Translator.ResolvePerAgentChainsAsync("ghost");

        var ex = await act.Should().ThrowAsync<ManifestInstantiationException>();
        ex.Which.Urn.Should().Be(ManifestInstantiationUrns.AgentNotFound);
    }

    [Fact]
    public async Task ResolvePerAgentChainsAsync_SecondCall_ReturnsCachedInstance()
    {
        var manifest = BuildManifest();
        var fixture = new TranslatorFixture()
            .WithManifest(manifest).WithProvider("openai");

        var first = await fixture.Translator.ResolvePerAgentChainsAsync(AgentId);
        var second = await fixture.Translator.ResolvePerAgentChainsAsync(AgentId);

        second.Should().BeSameAs(first);
    }

    [Fact]
    public async Task ResolvePerAgentChainsAsync_AfterInvalidate_RebuildsChain()
    {
        var manifest = BuildManifest();
        var fixture = new TranslatorFixture()
            .WithManifest(manifest).WithProvider("openai");

        var first = await fixture.Translator.ResolvePerAgentChainsAsync(AgentId);
        await fixture.Translator.InvalidateAsync(AgentId);
        var second = await fixture.Translator.ResolvePerAgentChainsAsync(AgentId);

        second.Should().NotBeSameAs(first, because: "InvalidateAsync should evict the per-agent chain cache too");
    }

    [Fact]
    public async Task ResolvePerAgentChainsAsync_PreservesManifestBudget()
    {
        var budget = new RunBudget(MaxTurns: 7, MaxDuration: TimeSpan.FromMinutes(2));
        var manifest = BuildManifest(budget: budget);
        var fixture = new TranslatorFixture()
            .WithManifest(manifest).WithProvider("openai");

        var chains = await fixture.Translator.ResolvePerAgentChainsAsync(AgentId);

        chains.Budget.Should().Be(budget);
    }

    // ── PAM-7 plugin-branch regression guards ──────────────────────────────────
    //
    // These fail against the pre-PAM-7 translator: the plugin branch used to set only
    // ToolGatewayMiddleware = DI-global on StatefulAgentOptions, silently dropping
    // LlmGatewayRef / McpGatewayRef / OntologyRef-bound south cartridge.

    [Fact]
    public async Task TranslateAsync_PluginAgent_WithLlmGatewayRef_StashesConfiguredLlmChainOnOptions()
    {
        var expected = new FakeLlmMiddleware();
        var (registry, factory) = BuildLlmGatewayConfig("llm-gw", expected);

        var manifest = BuildPluginManifest(PluginHandler, llmGatewayRef: "llm-gw");
        var fixture = new TranslatorFixture()
            .WithManifest(manifest)
            .WithPluginHandler(PluginHandler, (_, _) => new FakePluginAiAgent())
            .WithLlmGatewayConfigRegistry(registry)
            .WithLlmGatewayFactory(factory);

        var options = await fixture.Translator.TranslateAsync(AgentId);

        options.GatewayMiddleware.Should().ContainSingle()
            .Which.Should().BeSameAs(expected,
                because: "PAM-7: plugin agent's LlmGatewayRef must populate options.GatewayMiddleware");
    }

    [Fact]
    public async Task TranslateAsync_PluginAgent_WithOntologyRef_StashesSouthCartridgeOnOptions()
    {
        // The G7 trajectory bypass symptom: a plugin agent bound to an OntologyRef-bound MCP server
        // must see the south cartridge on its tool chain. Pre-PAM-7 the plugin branch silently
        // dropped this; this test passes only after PAM-7.
        const string virtualServerId = "k8s-virtual";
        var ontologyRegistry = new InMemoryDomainOntologyArtifactRegistry();
        ontologyRegistry.Register("k8s-tools-v1", new DomainOntologyArtifact { OntologyVersion = "v1" });

        var virtualServer = new McpServerManifest(virtualServerId, "1.0")
        {
            Virtual = true,
            Sources = [new McpServerSourceRef("dummy-upstream")],
            OntologyRef = "k8s-tools-v1",
        };
        var serverRegistry = Substitute.For<IMcpServerRegistry>();
        serverRegistry.GetAsync(virtualServerId, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpServerManifest?>(virtualServer));

        var manifest = BuildPluginManifest(
            PluginHandler,
            mcpServers: [new McpServerRef(virtualServerId, McpServerRef.RegisteredTransport)]);

        var fixture = new TranslatorFixture()
            .WithManifest(manifest)
            .WithPluginHandler(PluginHandler, (_, _) => new FakePluginAiAgent())
            .WithMcpServerRegistry(serverRegistry)
            .WithToolSourceProvider(new EmptyNamedToolSourceProvider("dummy-upstream"))
            .WithDomainOntologyRegistry(ontologyRegistry);

        var options = await fixture.Translator.TranslateAsync(AgentId);

        var names = options.ToolGatewayMiddleware.Select(m => m.GetType().Name).ToList();
        names.Should().Contain(nameof(DomainOntologyArgValidationMiddleware));
        names.Should().Contain(nameof(DomainOntologyResponseEnrichmentMiddleware));
    }

    [Fact]
    public async Task ResolvePerAgentChainsAsync_PluginAgent_WithLlmGatewayRef_ReturnsConfiguredChain()
    {
        // Container-gateway endpoints will call this surface on every LLM callback for a plugin
        // agent. Symmetric with the TranslateAsync test above — guards the path that the
        // container endpoints take, independent of the grain-side TranslateAsync cache.
        var expected = new FakeLlmMiddleware();
        var (registry, factory) = BuildLlmGatewayConfig("llm-gw", expected);

        var manifest = BuildPluginManifest(PluginHandler, llmGatewayRef: "llm-gw");
        var fixture = new TranslatorFixture()
            .WithManifest(manifest)
            .WithPluginHandler(PluginHandler, (_, _) => new FakePluginAiAgent())
            .WithLlmGatewayConfigRegistry(registry)
            .WithLlmGatewayFactory(factory);

        var chains = await fixture.Translator.ResolvePerAgentChainsAsync(AgentId);

        chains.Llm.Should().ContainSingle().Which.Should().BeSameAs(expected);
    }

    // ── G3 ResolveAgentToolsAsync — manifest-scoped tool discovery ─────────────

    [Fact]
    public async Task ResolveAgentToolsAsync_ReturnsOnlyManifestDeclaredTools()
    {
        var manifest = new AgentManifest(
            Id: AgentId, Version: Version,
            Handler: new AgentHandlerRef("declarative"),
            Protocols: [],
            Tools: [new ToolRef("greeter", "static:greeter")])
        {
            Model = new ModelSpec("openai", "gpt-4o"),
        };

        var fixture = new TranslatorFixture()
            .WithManifest(manifest).WithProvider("openai")
            .WithStaticTool("greeter", new TinyTool("greeter"));

        var tools = await fixture.Translator.ResolveAgentToolsAsync(AgentId);

        tools.Should().ContainSingle().Which.Name.Should().Be("greeter");
    }

    [Fact]
    public async Task ResolveAgentToolsAsync_NoToolsInManifest_ReturnsEmpty()
    {
        var manifest = new AgentManifest(
            Id: AgentId, Version: Version,
            Handler: new AgentHandlerRef("declarative"),
            Protocols: [],
            Tools: [])
        {
            Model = new ModelSpec("openai", "gpt-4o"),
        };

        var fixture = new TranslatorFixture()
            .WithManifest(manifest).WithProvider("openai");

        var tools = await fixture.Translator.ResolveAgentToolsAsync(AgentId);

        tools.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAgentToolsAsync_PluginAgent_ReturnsOnlyManifestDeclaredTools()
    {
        // The G3 regression guard for plugin agents: even though plugin agents have their own
        // IAiAgent impl, the container-gateway tools/list endpoint must see only the manifest
        // surface, not the bulk registry.
        var manifest = new AgentManifest(
            Id: AgentId, Version: Version,
            Handler: new AgentHandlerRef(PluginHandler),
            Protocols: [],
            Tools: [new ToolRef("greeter", "static:greeter")]);

        var fixture = new TranslatorFixture()
            .WithManifest(manifest)
            .WithPluginHandler(PluginHandler, (_, _) => new FakePluginAiAgent())
            .WithStaticTool("greeter", new TinyTool("greeter"));

        var tools = await fixture.Translator.ResolveAgentToolsAsync(AgentId);

        tools.Should().ContainSingle().Which.Name.Should().Be("greeter");
    }

    [Fact]
    public async Task ResolveAgentToolsAsync_UnknownAgent_ThrowsAgentNotFound()
    {
        var fixture = new TranslatorFixture().WithProvider("openai");

        var act = async () => await fixture.Translator.ResolveAgentToolsAsync("ghost");

        var ex = await act.Should().ThrowAsync<ManifestInstantiationException>();
        ex.Which.Urn.Should().Be(ManifestInstantiationUrns.AgentNotFound);
    }

    [Fact]
    public async Task ResolveAgentToolsAsync_SecondCall_ReturnsCachedInstance()
    {
        var manifest = new AgentManifest(
            Id: AgentId, Version: Version,
            Handler: new AgentHandlerRef("declarative"),
            Protocols: [],
            Tools: [new ToolRef("greeter", "static:greeter")])
        {
            Model = new ModelSpec("openai", "gpt-4o"),
        };
        var fixture = new TranslatorFixture()
            .WithManifest(manifest).WithProvider("openai")
            .WithStaticTool("greeter", new TinyTool("greeter"));

        var first = await fixture.Translator.ResolveAgentToolsAsync(AgentId);
        var second = await fixture.Translator.ResolveAgentToolsAsync(AgentId);

        second.Should().BeSameAs(first);
    }

    [Fact]
    public async Task ResolveAgentToolsAsync_AfterInvalidate_RebuildsList()
    {
        var manifest = new AgentManifest(
            Id: AgentId, Version: Version,
            Handler: new AgentHandlerRef("declarative"),
            Protocols: [],
            Tools: [new ToolRef("greeter", "static:greeter")])
        {
            Model = new ModelSpec("openai", "gpt-4o"),
        };
        var fixture = new TranslatorFixture()
            .WithManifest(manifest).WithProvider("openai")
            .WithStaticTool("greeter", new TinyTool("greeter"));

        var first = await fixture.Translator.ResolveAgentToolsAsync(AgentId);
        await fixture.Translator.InvalidateAsync(AgentId);
        var second = await fixture.Translator.ResolveAgentToolsAsync(AgentId);

        second.Should().NotBeSameAs(first, because: "InvalidateAsync must evict the per-agent tools cache too");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static (ILlmGatewayConfigRegistry Registry, ILlmGatewayMiddlewareFactory Factory)
        BuildLlmGatewayConfig(string refName, LlmGatewayMiddleware middleware)
    {
        var cfg = new LlmGatewayConfigManifest(
            refName, "1",
            [new GatewayMiddlewareSpec("Probe")]);
        var registry = Substitute.For<ILlmGatewayConfigRegistry>();
        registry.GetAsync(refName, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<LlmGatewayConfigManifest?>(cfg));
        var factory = Substitute.For<ILlmGatewayMiddlewareFactory>();
        factory.Create(Arg.Any<GatewayMiddlewareSpec>()).Returns(middleware);
        return (registry, factory);
    }

    private static (IMcpGatewayConfigRegistry Registry, IToolGatewayMiddlewareFactory Factory)
        BuildMcpGatewayConfig(string refName, ToolGatewayMiddleware middleware)
    {
        var cfg = new McpGatewayConfigManifest(
            refName, "1",
            [new GatewayMiddlewareSpec("Probe")]);
        var registry = Substitute.For<IMcpGatewayConfigRegistry>();
        registry.GetAsync(refName, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpGatewayConfigManifest?>(cfg));
        var factory = Substitute.For<IToolGatewayMiddlewareFactory>();
        factory.Create(Arg.Any<GatewayMiddlewareSpec>()).Returns(middleware);
        return (registry, factory);
    }

    private static AgentManifest BuildManifest(
        IReadOnlyList<McpServerRef>? mcpServers = null,
        string? llmGatewayRef = null,
        string? mcpGatewayRef = null,
        RunBudget? budget = null)
    {
        return new AgentManifest(
            Id: AgentId, Version: Version,
            Handler: new AgentHandlerRef("declarative"),
            Protocols: [],
            Tools: [])
        {
            Model = new ModelSpec("openai", "gpt-4o"),
            McpServers = mcpServers,
            LlmGatewayRef = llmGatewayRef,
            McpGatewayRef = mcpGatewayRef,
            Budget = budget,
        };
    }

    private static AgentManifest BuildPluginManifest(
        string handlerTypeName,
        IReadOnlyList<McpServerRef>? mcpServers = null,
        string? llmGatewayRef = null,
        string? mcpGatewayRef = null)
    {
        return new AgentManifest(
            Id: AgentId, Version: Version,
            Handler: new AgentHandlerRef(handlerTypeName),
            Protocols: [],
            Tools: [])
        {
            McpServers = mcpServers,
            LlmGatewayRef = llmGatewayRef,
            McpGatewayRef = mcpGatewayRef,
        };
    }

    private sealed class FakeLlmMiddleware : LlmGatewayMiddleware { }

    private sealed class FakeToolMiddleware : ToolGatewayMiddleware { }

    private sealed class TinyTool(string name) : ITool
    {
        public string Name { get; } = name;
        public string Description => "tiny";
        public System.Text.Json.JsonElement ParametersSchema { get; }
            = System.Text.Json.JsonDocument.Parse("{\"type\":\"object\"}").RootElement;
        public Task<string> InvokeAsync(System.Text.Json.JsonElement arguments, CancellationToken cancellationToken = default)
            => Task.FromResult("ok");
    }

    private sealed class FakePluginAiAgent : IAiAgent
    {
        public string? SystemPrompt { get; set; }
        public IAgentSession Session { get; } = new FakeSession();
        public IReadOnlyList<ChatTurn> History => Session.History;
        public Task<string> AskAsync(string userMessage, CancellationToken cancellationToken = default)
            => Task.FromResult("reply");
        public void Reset() { }
    }

    private sealed class FakeSession : IAgentSession
    {
        private readonly List<ChatTurn> _history = new();
        public string SessionId => "session-1";
        public string AgentId => AgentManifestTranslatorPerAgentChainsTests.AgentId;
        public IReadOnlyList<ChatTurn> History => _history;
        public ValueTask AppendAsync(ChatTurn turn, CancellationToken cancellationToken = default)
        {
            _history.Add(turn);
            return ValueTask.CompletedTask;
        }
        public ValueTask ResetAsync(CancellationToken cancellationToken = default)
        {
            _history.Clear();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class EmptyNamedToolSourceProvider(string name) : INamedToolSourceProvider
    {
        public IToolSource? GetByName(string serverName)
            => string.Equals(serverName, name, StringComparison.Ordinal)
                ? new EmptyToolSource()
                : null;

        private sealed class EmptyToolSource : IToolSource
        {
            public async IAsyncEnumerable<ITool> DiscoverAsync(
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.CompletedTask;
                yield break;
            }
        }
    }
}
