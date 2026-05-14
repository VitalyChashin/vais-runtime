// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Vais.Agents.Core;
using Vais.Agents.Gateways.McpGovernance;
using Xunit;

namespace Vais.Agents.Runtime.Instantiation.Tests;

/// <summary>
/// Phase 3 tests: GCF-20–25.
/// Covers the named middleware factory layer, per-agent gateway pipeline resolution,
/// ToolWorkspacePolicy injection, transport:registered expansion, and VirtualMcpToolSource.
/// </summary>
public class GatewayPhase3Tests
{
    private const string AgentId = "agent-gw";
    private const string Version = "1.0";

    // ── Tests 1–2: DefaultLlmGatewayMiddlewareFactory ─────────────────────────

    [Fact]
    public void DefaultLlmFactory_Resolves_Registered_Middleware_By_Name()
    {
        var expected = new FakeLlmMiddleware();
        var registrations = new[]
        {
            new NamedLlmGatewayMiddlewareRegistration("Prometheus", (_, _) => expected),
            new NamedLlmGatewayMiddlewareRegistration("Fallback",   (_, _) => new FakeLlmMiddleware()),
        };
        var factory = new DefaultLlmGatewayMiddlewareFactory(registrations,
            Substitute.For<IServiceProvider>());

        var result = factory.Create(new GatewayMiddlewareSpec("Prometheus"));

        result.Should().BeSameAs(expected);
    }

    [Fact]
    public void DefaultLlmFactory_Unknown_Name_Throws_With_Known_Names_Listed()
    {
        var registrations = new[]
        {
            new NamedLlmGatewayMiddlewareRegistration("Prometheus", (_, _) => new FakeLlmMiddleware()),
        };
        var factory = new DefaultLlmGatewayMiddlewareFactory(registrations,
            Substitute.For<IServiceProvider>());

        var act = () => factory.Create(new GatewayMiddlewareSpec("Ghost"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Ghost*")
            .WithMessage("*Prometheus*");
    }

    // ── Test 3: LlmGatewayRef → per-agent pipeline ────────────────────────────

    [Fact]
    public async Task Translator_LlmGatewayRef_Resolves_Per_Agent_Pipeline()
    {
        var expected = new FakeLlmMiddleware();
        var llmCfg = new LlmGatewayConfigManifest(
            "llm-gw", "1",
            new[] { new GatewayMiddlewareSpec("Prometheus") });

        var registry = Substitute.For<ILlmGatewayConfigRegistry>();
        registry.GetAsync("llm-gw", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<LlmGatewayConfigManifest?>(llmCfg));

        var llmFactory = Substitute.For<ILlmGatewayMiddlewareFactory>();
        llmFactory.Create(Arg.Any<GatewayMiddlewareSpec>()).Returns(expected);

        var manifest = BuildManifest(llmGatewayRef: "llm-gw");
        var fixture = new TranslatorFixture()
            .WithManifest(manifest).WithProvider("openai")
            .WithLlmGatewayConfigRegistry(registry)
            .WithLlmGatewayFactory(llmFactory);

        var options = await fixture.Translator.TranslateAsync(AgentId);

        options.GatewayMiddleware.Should().HaveCount(1);
        options.GatewayMiddleware[0].Should().BeSameAs(expected);
    }

    // ── Test 4: No LlmGatewayRef → DI-global chain ────────────────────────────

    [Fact]
    public async Task Translator_No_LlmGatewayRef_Uses_DI_Global_Chain()
    {
        var globalMw = new FakeLlmMiddleware();
        var manifest = BuildManifest();
        var fixture = new TranslatorFixture()
            .WithManifest(manifest).WithProvider("openai")
            .WithDiGlobalLlmMiddleware(globalMw);

        var options = await fixture.Translator.TranslateAsync(AgentId);

        options.GatewayMiddleware.Should().HaveCount(1);
        options.GatewayMiddleware[0].Should().BeSameAs(globalMw);
    }

    // ── Test 5: McpGatewayRef → per-agent tool pipeline ───────────────────────

    [Fact]
    public async Task Translator_McpGatewayRef_Resolves_Per_Agent_Tool_Pipeline()
    {
        var expected = new FakeToolMiddleware();
        var mcpCfg = new McpGatewayConfigManifest(
            "mcp-gw", "1",
            new[] { new GatewayMiddlewareSpec("ToolRetry") });

        var registry = Substitute.For<IMcpGatewayConfigRegistry>();
        registry.GetAsync("mcp-gw", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpGatewayConfigManifest?>(mcpCfg));

        var toolFactory = Substitute.For<IToolGatewayMiddlewareFactory>();
        toolFactory.Create(Arg.Any<GatewayMiddlewareSpec>()).Returns(expected);

        var manifest = BuildManifest(mcpGatewayRef: "mcp-gw");
        var fixture = new TranslatorFixture()
            .WithManifest(manifest).WithProvider("openai")
            .WithMcpGatewayConfigRegistry(registry)
            .WithToolGatewayFactory(toolFactory);

        var options = await fixture.Translator.TranslateAsync(AgentId);

        options.ToolGatewayMiddleware.Should().HaveCount(1);
        options.ToolGatewayMiddleware[0].Should().BeSameAs(expected);
    }

    // ── Test 6: ToolWorkspacePolicy sentinel → injected from manifest ─────────

    [Fact]
    public async Task Translator_McpWorkspacePolicy_Injected_Into_ToolWorkspacePolicyMiddleware()
    {
        var loggingMw = new FakeToolMiddleware();
        var mcpCfg = new McpGatewayConfigManifest(
            "mcp-gw", "1",
            new[]
            {
                new GatewayMiddlewareSpec("ToolWorkspacePolicy"),
                new GatewayMiddlewareSpec("ToolLogging"),
            })
        {
            WorkspacePolicies = new Dictionary<string, McpWorkspacePolicySpec>
            {
                ["ws1"] = new McpWorkspacePolicySpec(AllowedTools: new[] { "safe_" }),
            }
        };

        var registry = Substitute.For<IMcpGatewayConfigRegistry>();
        registry.GetAsync("mcp-gw", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpGatewayConfigManifest?>(mcpCfg));

        var toolFactory = Substitute.For<IToolGatewayMiddlewareFactory>();
        toolFactory.Create(Arg.Is<GatewayMiddlewareSpec>(s => s.Name != "ToolWorkspacePolicy"))
            .Returns(loggingMw);

        var manifest = BuildManifest(mcpGatewayRef: "mcp-gw");
        var fixture = new TranslatorFixture()
            .WithManifest(manifest).WithProvider("openai")
            .WithMcpGatewayConfigRegistry(registry)
            .WithToolGatewayFactory(toolFactory);

        var options = await fixture.Translator.TranslateAsync(AgentId);

        options.ToolGatewayMiddleware.Should().HaveCount(2);
        options.ToolGatewayMiddleware[0].Should().BeOfType<ToolWorkspacePolicyMiddleware>();
        options.ToolGatewayMiddleware[1].Should().BeSameAs(loggingMw);
        // Factory must NOT be called for the sentinel spec
        toolFactory.DidNotReceive().Create(Arg.Is<GatewayMiddlewareSpec>(s => s.Name == "ToolWorkspacePolicy"));
    }

    // ── Test 7: transport:registered physical server → INamedToolSourceProvider ─

    [Fact]
    public async Task Translator_Registered_Physical_Server_Resolved_Via_NamedProvider()
    {
        var tool = new FakeTool("echo");
        var source = new FakeToolSource(tool);
        var provider = new FakeNamedToolSourceProvider(("physical-srv", source));

        var serverManifest = new McpServerManifest("physical-srv", "1");  // Virtual=false (default)

        var serverRegistry = Substitute.For<IMcpServerRegistry>();
        serverRegistry.GetAsync("physical-srv", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpServerManifest?>(serverManifest));

        var manifest = BuildManifest(
            tools: new[] { new ToolRef("echo", "mcp:physical-srv") },
            mcpServers: new[] { new McpServerRef("physical-srv", McpServerRef.RegisteredTransport) });

        var fixture = new TranslatorFixture()
            .WithManifest(manifest).WithProvider("openai")
            .WithMcpServerRegistry(serverRegistry)
            .WithToolSourceProvider(provider);

        var options = await fixture.Translator.TranslateAsync(AgentId);

        options.ToolRegistry.Should().NotBeNull();
        options.ToolRegistry!.Tools.Should().ContainSingle(t => t.Name == "echo");
    }

    // ── Test 8: transport:registered virtual server → VirtualMcpToolSource ─────

    [Fact]
    public async Task Translator_Registered_Virtual_Server_Builds_VirtualMcpToolSource()
    {
        var tool = new FakeTool("aggregate");
        var upstreamSource = new FakeToolSource(tool);
        var provider = new FakeNamedToolSourceProvider(("upstream1", upstreamSource));

        var virtualManifest = new McpServerManifest("virtual-srv", "1")
        {
            Virtual = true,
            Sources = new[] { new McpServerSourceRef("upstream1") },
        };

        var serverRegistry = Substitute.For<IMcpServerRegistry>();
        serverRegistry.GetAsync("virtual-srv", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpServerManifest?>(virtualManifest));

        var manifest = BuildManifest(
            tools: new[] { new ToolRef("aggregate", "mcp:virtual-srv") },
            mcpServers: new[] { new McpServerRef("virtual-srv", McpServerRef.RegisteredTransport) });

        var fixture = new TranslatorFixture()
            .WithManifest(manifest).WithProvider("openai")
            .WithMcpServerRegistry(serverRegistry)
            .WithToolSourceProvider(provider);

        var options = await fixture.Translator.TranslateAsync(AgentId);

        options.ToolRegistry.Should().NotBeNull();
        options.ToolRegistry!.Tools.Should().ContainSingle(t => t.Name == "aggregate");
    }

    // ── Tests 9–10: VirtualMcpToolSource ──────────────────────────────────────

    [Fact]
    public async Task VirtualMcpToolSource_Projection_Exposes_Renamed_Tool()
    {
        var upstream = new FakeToolSource(
            new FakeTool("raw_tool"),
            new FakeTool("other_tool"));

        var projection = new[]
        {
            new McpServerToolProjection(Name: "exposed_tool", From: "src1", SourceToolName: "raw_tool"),
        };

        var vts = new VirtualMcpToolSource(
            new[] { (Source: (IToolSource)upstream, ServerId: "src1") },
            projection);

        var tools = await CollectToolsAsync(vts);

        tools.Should().HaveCount(1);
        tools[0].Name.Should().Be("exposed_tool");
    }

    [Fact]
    public async Task VirtualMcpToolSource_No_Projection_All_Tools_First_Source_Wins_On_Collision()
    {
        var src1 = new FakeToolSource(new FakeTool("tool_a"), new FakeTool("shared"));
        var src2 = new FakeToolSource(new FakeTool("tool_b"), new FakeTool("shared"));  // "shared" is a collision

        var vts = new VirtualMcpToolSource(
            new[]
            {
                (Source: (IToolSource)src1, ServerId: "s1"),
                (Source: (IToolSource)src2, ServerId: "s2"),
            },
            projection: null);

        var tools = await CollectToolsAsync(vts);

        tools.Select(t => t.Name).Should().BeEquivalentTo(new[] { "tool_a", "shared", "tool_b" });
    }

    // ── Test 11: LlmGatewayRef missing from registry ──────────────────────────

    [Fact]
    public async Task Translator_LlmGatewayRef_Not_In_Registry_Throws()
    {
        var registry = Substitute.For<ILlmGatewayConfigRegistry>();
        registry.GetAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<LlmGatewayConfigManifest?>((LlmGatewayConfigManifest?)null));

        var llmFactory = Substitute.For<ILlmGatewayMiddlewareFactory>();

        var manifest = BuildManifest(llmGatewayRef: "missing-llm-gw");
        var fixture = new TranslatorFixture()
            .WithManifest(manifest).WithProvider("openai")
            .WithLlmGatewayConfigRegistry(registry)
            .WithLlmGatewayFactory(llmFactory);

        var act = async () => await fixture.Translator.TranslateAsync(AgentId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*missing-llm-gw*");
    }

    // ── Test 12: McpGatewayRef missing from registry ──────────────────────────

    [Fact]
    public async Task Translator_McpGatewayRef_Not_In_Registry_Throws()
    {
        var registry = Substitute.For<IMcpGatewayConfigRegistry>();
        registry.GetAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpGatewayConfigManifest?>((McpGatewayConfigManifest?)null));

        var toolFactory = Substitute.For<IToolGatewayMiddlewareFactory>();

        var manifest = BuildManifest(mcpGatewayRef: "missing-mcp-gw");
        var fixture = new TranslatorFixture()
            .WithManifest(manifest).WithProvider("openai")
            .WithMcpGatewayConfigRegistry(registry)
            .WithToolGatewayFactory(toolFactory);

        var act = async () => await fixture.Translator.TranslateAsync(AgentId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*missing-mcp-gw*");
    }

    // ── Tests 13–20: Option B import-all mode (VSB-1 / VSB-2 / VSB-3) ──────────

    [Fact]
    public async Task ImportAll_Virtual_Server_No_Tools_Entry_Imports_All_Tools()
    {
        var upstreamSource = new FakeToolSource(new FakeTool("tool-a"), new FakeTool("tool-b"));
        var provider = new FakeNamedToolSourceProvider(("upstream", upstreamSource));

        var virtualManifest = new McpServerManifest("virtual-srv", "1")
        {
            Virtual = true,
            Sources = new[] { new McpServerSourceRef("upstream") },
        };

        var serverRegistry = Substitute.For<IMcpServerRegistry>();
        serverRegistry.GetAsync("virtual-srv", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpServerManifest?>(virtualManifest));

        // No tools[] → import-all mode
        var manifest = BuildManifest(
            mcpServers: new[] { new McpServerRef("virtual-srv", McpServerRef.RegisteredTransport) });

        var fixture = new TranslatorFixture()
            .WithManifest(manifest).WithProvider("openai")
            .WithMcpServerRegistry(serverRegistry)
            .WithToolSourceProvider(provider);

        var options = await fixture.Translator.TranslateAsync(AgentId);

        options.ToolRegistry.Should().NotBeNull();
        options.ToolRegistry!.Tools.Select(t => t.Name)
            .Should().BeEquivalentTo(new[] { "tool-a", "tool-b" });
    }

    [Fact]
    public async Task ImportAll_Physical_Server_No_Tools_Entry_Imports_All_Tools()
    {
        var source = new FakeToolSource(new FakeTool("search"), new FakeTool("fetch"));
        var provider = new FakeNamedToolSourceProvider(("physical-srv", source));

        var serverManifest = new McpServerManifest("physical-srv", "1");
        var serverRegistry = Substitute.For<IMcpServerRegistry>();
        serverRegistry.GetAsync("physical-srv", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpServerManifest?>(serverManifest));

        // No tools[] → import-all mode
        var manifest = BuildManifest(
            mcpServers: new[] { new McpServerRef("physical-srv", McpServerRef.RegisteredTransport) });

        var fixture = new TranslatorFixture()
            .WithManifest(manifest).WithProvider("openai")
            .WithMcpServerRegistry(serverRegistry)
            .WithToolSourceProvider(provider);

        var options = await fixture.Translator.TranslateAsync(AgentId);

        options.ToolRegistry.Should().NotBeNull();
        options.ToolRegistry!.Tools.Select(t => t.Name)
            .Should().BeEquivalentTo(new[] { "search", "fetch" });
    }

    [Fact]
    public async Task ImportAll_Explicit_Tools_Entry_Keeps_Explicit_Mode_Backward_Compat()
    {
        // D1: any tools[] entry referencing the server puts it in explicit mode → only listed tools.
        var source = new FakeToolSource(new FakeTool("search"), new FakeTool("fetch"), new FakeTool("analyze"));
        var provider = new FakeNamedToolSourceProvider(("srv", source));

        var serverManifest = new McpServerManifest("srv", "1");
        var serverRegistry = Substitute.For<IMcpServerRegistry>();
        serverRegistry.GetAsync("srv", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpServerManifest?>(serverManifest));

        var manifest = BuildManifest(
            tools: new[] { new ToolRef("search", "mcp:srv") },
            mcpServers: new[] { new McpServerRef("srv", McpServerRef.RegisteredTransport) });

        var fixture = new TranslatorFixture()
            .WithManifest(manifest).WithProvider("openai")
            .WithMcpServerRegistry(serverRegistry)
            .WithToolSourceProvider(provider);

        var options = await fixture.Translator.TranslateAsync(AgentId);

        options.ToolRegistry.Should().NotBeNull();
        options.ToolRegistry!.Tools.Should().ContainSingle(t => t.Name == "search");
    }

    [Fact]
    public async Task ImportAll_McpServerRef_Tools_Allowlist_Narrows_Import()
    {
        // D2: McpServerRef.Tools allowlist restricts the import to only those tool names.
        var source = new FakeToolSource(
            new FakeTool("tool-1"), new FakeTool("tool-2"), new FakeTool("tool-3"),
            new FakeTool("tool-4"), new FakeTool("tool-5"));
        var provider = new FakeNamedToolSourceProvider(("srv", source));

        var serverManifest = new McpServerManifest("srv", "1");
        var serverRegistry = Substitute.For<IMcpServerRegistry>();
        serverRegistry.GetAsync("srv", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpServerManifest?>(serverManifest));

        var manifest = BuildManifest(
            mcpServers: new[]
            {
                new McpServerRef("srv", McpServerRef.RegisteredTransport,
                    Tools: new[] { "tool-1", "tool-3" }),
            });

        var fixture = new TranslatorFixture()
            .WithManifest(manifest).WithProvider("openai")
            .WithMcpServerRegistry(serverRegistry)
            .WithToolSourceProvider(provider);

        var options = await fixture.Translator.TranslateAsync(AgentId);

        options.ToolRegistry.Should().NotBeNull();
        options.ToolRegistry!.Tools.Select(t => t.Name)
            .Should().BeEquivalentTo(new[] { "tool-1", "tool-3" });
    }

    [Fact]
    public async Task ImportAll_McpServerRef_Tools_Allowlist_Missing_Tool_Throws_McpToolNotFound()
    {
        // D2: an allowlisted name not exposed by the server → McpToolNotFound.
        var source = new FakeToolSource(new FakeTool("a"), new FakeTool("b"), new FakeTool("c"));
        var provider = new FakeNamedToolSourceProvider(("srv", source));

        var serverManifest = new McpServerManifest("srv", "1");
        var serverRegistry = Substitute.For<IMcpServerRegistry>();
        serverRegistry.GetAsync("srv", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpServerManifest?>(serverManifest));

        var manifest = BuildManifest(
            mcpServers: new[]
            {
                new McpServerRef("srv", McpServerRef.RegisteredTransport,
                    Tools: new[] { "a", "ghost-tool" }),
            });

        var fixture = new TranslatorFixture()
            .WithManifest(manifest).WithProvider("openai")
            .WithMcpServerRegistry(serverRegistry)
            .WithToolSourceProvider(provider);

        var act = async () => await fixture.Translator.TranslateAsync(AgentId);

        await act.Should().ThrowAsync<ManifestInstantiationException>()
            .Where(e => e.Urn == ManifestInstantiationUrns.McpToolNotFound)
            .WithMessage("*ghost-tool*");
    }

    [Fact]
    public async Task ImportAll_Two_Servers_Shared_Tool_Name_Throws_McpToolNameCollision()
    {
        // D3: two import-all servers sharing a tool name → McpToolNameCollision naming both.
        var sourceA = new FakeToolSource(new FakeTool("foo"), new FakeTool("bar"));
        var sourceB = new FakeToolSource(new FakeTool("foo"), new FakeTool("baz"));
        var provider = new FakeNamedToolSourceProvider(
            ("server-a", sourceA),
            ("server-b", sourceB));

        var manifestA = new McpServerManifest("server-a", "1");
        var manifestB = new McpServerManifest("server-b", "1");
        var serverRegistry = Substitute.For<IMcpServerRegistry>();
        serverRegistry.GetAsync("server-a", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpServerManifest?>(manifestA));
        serverRegistry.GetAsync("server-b", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpServerManifest?>(manifestB));

        var manifest = BuildManifest(mcpServers: new[]
        {
            new McpServerRef("server-a", McpServerRef.RegisteredTransport),
            new McpServerRef("server-b", McpServerRef.RegisteredTransport),
        });

        var fixture = new TranslatorFixture()
            .WithManifest(manifest).WithProvider("openai")
            .WithMcpServerRegistry(serverRegistry)
            .WithToolSourceProvider(provider);

        var act = async () => await fixture.Translator.TranslateAsync(AgentId);

        await act.Should().ThrowAsync<ManifestInstantiationException>()
            .Where(e => e.Urn == ManifestInstantiationUrns.McpToolNameCollision)
            .WithMessage("*foo*")
            .WithMessage("*server-a*")
            .WithMessage("*server-b*");
    }

    [Fact]
    public async Task ImportAll_Explicit_Entry_Puts_Server_In_Explicit_Mode_No_Collision()
    {
        // D1 + D3: explicit tools[] entry on server-a puts it in explicit mode.
        // server-a: explicit → only "bar" imported; server-b: import-all → "foo" + "baz".
        // No collision because server-a no longer contributes "foo" via import-all.
        var sourceA = new FakeToolSource(new FakeTool("foo"), new FakeTool("bar"));
        var sourceB = new FakeToolSource(new FakeTool("foo"), new FakeTool("baz"));
        var provider = new FakeNamedToolSourceProvider(
            ("server-a", sourceA),
            ("server-b", sourceB));

        var manifestA = new McpServerManifest("server-a", "1");
        var manifestB = new McpServerManifest("server-b", "1");
        var serverRegistry = Substitute.For<IMcpServerRegistry>();
        serverRegistry.GetAsync("server-a", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpServerManifest?>(manifestA));
        serverRegistry.GetAsync("server-b", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpServerManifest?>(manifestB));

        var manifest = BuildManifest(
            tools: new[] { new ToolRef("bar", "mcp:server-a") },
            mcpServers: new[]
            {
                new McpServerRef("server-a", McpServerRef.RegisteredTransport),
                new McpServerRef("server-b", McpServerRef.RegisteredTransport),
            });

        var fixture = new TranslatorFixture()
            .WithManifest(manifest).WithProvider("openai")
            .WithMcpServerRegistry(serverRegistry)
            .WithToolSourceProvider(provider);

        var options = await fixture.Translator.TranslateAsync(AgentId);

        options.ToolRegistry.Should().NotBeNull();
        options.ToolRegistry!.Tools.Select(t => t.Name)
            .Should().BeEquivalentTo(new[] { "bar", "foo", "baz" });
    }

    [Fact]
    public async Task ImportAll_Mixed_With_Static_Tool_All_Resolve()
    {
        // import-all registered server + explicit static: tool → both resolve together.
        var source = new FakeToolSource(new FakeTool("search"));
        var provider = new FakeNamedToolSourceProvider(("srv", source));
        var staticTool = new FakeTool("calculate");

        var serverManifest = new McpServerManifest("srv", "1");
        var serverRegistry = Substitute.For<IMcpServerRegistry>();
        serverRegistry.GetAsync("srv", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpServerManifest?>(serverManifest));

        // tools[] has only the static tool — "srv" has no mcp: entry → import-all mode for srv.
        var manifest = BuildManifest(
            tools: new[] { new ToolRef("calculate", "static:calculate") },
            mcpServers: new[] { new McpServerRef("srv", McpServerRef.RegisteredTransport) });

        var fixture = new TranslatorFixture()
            .WithManifest(manifest).WithProvider("openai")
            .WithMcpServerRegistry(serverRegistry)
            .WithToolSourceProvider(provider)
            .WithStaticTool("calculate", staticTool);

        var options = await fixture.Translator.TranslateAsync(AgentId);

        options.ToolRegistry.Should().NotBeNull();
        options.ToolRegistry!.Tools.Select(t => t.Name)
            .Should().BeEquivalentTo(new[] { "calculate", "search" });
    }

    // ── Tests 21–25: Option D — server-level McpGatewayRef inheritance ────────

    [Fact]
    public async Task ServerGatewayRef_Applied_When_Agent_Has_None()
    {
        // T21: agent omits mcpGatewayRef; bound virtual server carries one → server pipeline fires.
        var expected = new FakeToolMiddleware();
        var serverManifest = new McpServerManifest("fetch-srv", "1")
        {
            Virtual = true,
            McpGatewayRef = "server-gw",
        };
        var mcpCfg = new McpGatewayConfigManifest(
            "server-gw", "1",
            new[] { new GatewayMiddlewareSpec("ToolOtel") });

        var serverRegistry = Substitute.For<IMcpServerRegistry>();
        serverRegistry.GetAsync("fetch-srv", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpServerManifest?>(serverManifest));

        var gwRegistry = Substitute.For<IMcpGatewayConfigRegistry>();
        gwRegistry.GetAsync("server-gw", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpGatewayConfigManifest?>(mcpCfg));

        var toolFactory = Substitute.For<IToolGatewayMiddlewareFactory>();
        toolFactory.Create(Arg.Any<GatewayMiddlewareSpec>()).Returns(expected);

        var manifest = BuildManifest(
            mcpServers: new[] { new McpServerRef("fetch-srv", McpServerRef.RegisteredTransport) });

        var fixture = new TranslatorFixture()
            .WithManifest(manifest).WithProvider("openai")
            .WithMcpServerRegistry(serverRegistry)
            .WithMcpGatewayConfigRegistry(gwRegistry)
            .WithToolGatewayFactory(toolFactory);

        var options = await fixture.Translator.TranslateAsync(AgentId);

        options.ToolGatewayMiddleware.Should().HaveCount(1);
        options.ToolGatewayMiddleware[0].Should().BeSameAs(expected);
    }

    [Fact]
    public async Task AgentGatewayRef_Wins_Over_Server_Ref()
    {
        // T22: agent has mcpGatewayRef; server also has one → agent pipeline fires; server config never loaded.
        var agentMw = new FakeToolMiddleware();
        var serverManifest = new McpServerManifest("fetch-srv", "1")
        {
            Virtual = true,
            McpGatewayRef = "server-gw",
        };
        var agentCfg = new McpGatewayConfigManifest(
            "agent-gw", "1",
            new[] { new GatewayMiddlewareSpec("AgentOtel") });

        var serverRegistry = Substitute.For<IMcpServerRegistry>();
        serverRegistry.GetAsync("fetch-srv", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpServerManifest?>(serverManifest));

        var gwRegistry = Substitute.For<IMcpGatewayConfigRegistry>();
        gwRegistry.GetAsync("agent-gw", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpGatewayConfigManifest?>(agentCfg));

        var toolFactory = Substitute.For<IToolGatewayMiddlewareFactory>();
        toolFactory.Create(Arg.Any<GatewayMiddlewareSpec>()).Returns(agentMw);

        var manifest = BuildManifest(
            mcpGatewayRef: "agent-gw",
            mcpServers: new[] { new McpServerRef("fetch-srv", McpServerRef.RegisteredTransport) });

        var fixture = new TranslatorFixture()
            .WithManifest(manifest).WithProvider("openai")
            .WithMcpServerRegistry(serverRegistry)
            .WithMcpGatewayConfigRegistry(gwRegistry)
            .WithToolGatewayFactory(toolFactory);

        var options = await fixture.Translator.TranslateAsync(AgentId);

        options.ToolGatewayMiddleware.Should().HaveCount(1);
        options.ToolGatewayMiddleware[0].Should().BeSameAs(agentMw);
        // Server's gateway config must not have been loaded.
        await gwRegistry.DidNotReceive().GetAsync("server-gw", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Two_Servers_Same_Ref_No_Ambiguity()
    {
        // T23: two registered servers both carry the same McpGatewayRef → single pipeline, no throw.
        var expected = new FakeToolMiddleware();
        var srvA = new McpServerManifest("srv-a", "1") { Virtual = true, McpGatewayRef = "shared-gw" };
        var srvB = new McpServerManifest("srv-b", "1") { Virtual = true, McpGatewayRef = "shared-gw" };
        var gwCfg = new McpGatewayConfigManifest(
            "shared-gw", "1",
            new[] { new GatewayMiddlewareSpec("ToolLogging") });

        var serverRegistry = Substitute.For<IMcpServerRegistry>();
        serverRegistry.GetAsync("srv-a", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpServerManifest?>(srvA));
        serverRegistry.GetAsync("srv-b", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpServerManifest?>(srvB));

        var gwRegistry = Substitute.For<IMcpGatewayConfigRegistry>();
        gwRegistry.GetAsync("shared-gw", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpGatewayConfigManifest?>(gwCfg));

        var toolFactory = Substitute.For<IToolGatewayMiddlewareFactory>();
        toolFactory.Create(Arg.Any<GatewayMiddlewareSpec>()).Returns(expected);

        var manifest = BuildManifest(mcpServers: new[]
        {
            new McpServerRef("srv-a", McpServerRef.RegisteredTransport),
            new McpServerRef("srv-b", McpServerRef.RegisteredTransport),
        });

        var fixture = new TranslatorFixture()
            .WithManifest(manifest).WithProvider("openai")
            .WithMcpServerRegistry(serverRegistry)
            .WithMcpGatewayConfigRegistry(gwRegistry)
            .WithToolGatewayFactory(toolFactory);

        var options = await fixture.Translator.TranslateAsync(AgentId);

        options.ToolGatewayMiddleware.Should().HaveCount(1);
        options.ToolGatewayMiddleware[0].Should().BeSameAs(expected);
    }

    [Fact]
    public async Task Two_Servers_Different_Refs_Throws_McpGatewayRefAmbiguous()
    {
        // T24: two registered servers carry different McpGatewayRef values, agent has none → throw.
        var srvA = new McpServerManifest("srv-a", "1") { Virtual = true, McpGatewayRef = "gw-alpha" };
        var srvB = new McpServerManifest("srv-b", "1") { Virtual = true, McpGatewayRef = "gw-beta" };

        var serverRegistry = Substitute.For<IMcpServerRegistry>();
        serverRegistry.GetAsync("srv-a", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpServerManifest?>(srvA));
        serverRegistry.GetAsync("srv-b", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpServerManifest?>(srvB));

        var gwRegistry = Substitute.For<IMcpGatewayConfigRegistry>();
        var toolFactory = Substitute.For<IToolGatewayMiddlewareFactory>();

        var manifest = BuildManifest(mcpServers: new[]
        {
            new McpServerRef("srv-a", McpServerRef.RegisteredTransport),
            new McpServerRef("srv-b", McpServerRef.RegisteredTransport),
        });

        var fixture = new TranslatorFixture()
            .WithManifest(manifest).WithProvider("openai")
            .WithMcpServerRegistry(serverRegistry)
            .WithMcpGatewayConfigRegistry(gwRegistry)
            .WithToolGatewayFactory(toolFactory);

        var act = async () => await fixture.Translator.TranslateAsync(AgentId);

        await act.Should().ThrowAsync<ManifestInstantiationException>()
            .Where(e => e.Urn == ManifestInstantiationUrns.McpGatewayRefAmbiguous)
            .WithMessage($"*{AgentId}*");
    }

    [Fact]
    public async Task No_Server_Ref_No_Agent_Ref_Falls_Back_To_DI_Global()
    {
        // T25: neither agent nor servers carry a gateway ref → DI-global chain fires.
        var globalMw = new FakeToolMiddleware();
        var serverManifest = new McpServerManifest("bare-srv", "1") { Virtual = true };

        var serverRegistry = Substitute.For<IMcpServerRegistry>();
        serverRegistry.GetAsync("bare-srv", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpServerManifest?>(serverManifest));

        var manifest = BuildManifest(
            mcpServers: new[] { new McpServerRef("bare-srv", McpServerRef.RegisteredTransport) });

        var fixture = new TranslatorFixture()
            .WithManifest(manifest).WithProvider("openai")
            .WithMcpServerRegistry(serverRegistry)
            .WithDiGlobalToolMiddleware(globalMw);

        var options = await fixture.Translator.TranslateAsync(AgentId);

        options.ToolGatewayMiddleware.Should().HaveCount(1);
        options.ToolGatewayMiddleware[0].Should().BeSameAs(globalMw);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static async Task<List<ITool>> CollectToolsAsync(IToolSource source)
    {
        var results = new List<ITool>();
        await foreach (var tool in source.DiscoverAsync())
            results.Add(tool);
        return results;
    }

    private static AgentManifest BuildManifest(
        string id = AgentId,
        IReadOnlyList<ToolRef>? tools = null,
        IReadOnlyList<McpServerRef>? mcpServers = null,
        string? llmGatewayRef = null,
        string? mcpGatewayRef = null)
    {
        return new AgentManifest(
            Id: id,
            Version: Version,
            Handler: new AgentHandlerRef("declarative"),
            Protocols: Array.Empty<ProtocolBinding>(),
            Tools: tools ?? Array.Empty<ToolRef>())
        {
            Model = new ModelSpec(Provider: "openai", Id: "gpt-4o"),
            McpServers = mcpServers,
            LlmGatewayRef = llmGatewayRef,
            McpGatewayRef = mcpGatewayRef,
        };
    }

    private sealed class FakeLlmMiddleware : LlmGatewayMiddleware { }

    private sealed class FakeToolMiddleware : ToolGatewayMiddleware { }

    private sealed class FakeTool : ITool
    {
        public FakeTool(string name)
        {
            Name = name;
            ParametersSchema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement;
        }

        public string Name { get; }
        public string Description => "fake";
        public JsonElement ParametersSchema { get; }
        public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
            => Task.FromResult("ok");
    }

    private sealed class FakeToolSource(params ITool[] tools) : IToolSource
    {
        public async IAsyncEnumerable<ITool> DiscoverAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var t in tools)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return t;
            }
            await Task.CompletedTask;
        }
    }

    private sealed class FakeNamedToolSourceProvider : INamedToolSourceProvider
    {
        private readonly Dictionary<string, IToolSource> _map;

        public FakeNamedToolSourceProvider(params (string Name, IToolSource Source)[] entries)
            => _map = entries.ToDictionary(e => e.Name, e => e.Source, StringComparer.Ordinal);

        public IToolSource? GetByName(string name) => _map.GetValueOrDefault(name);
    }
}
