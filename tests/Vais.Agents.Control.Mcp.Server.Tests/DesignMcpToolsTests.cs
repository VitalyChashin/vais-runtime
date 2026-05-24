// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Control.Mcp.Server.Tests;

/// <summary>
/// ND-6 guard — vais.list, vais.get, vais.describe, vais.diff tool handlers.
/// Uses in-memory stubs for registries to keep tests fast + hermetic.
/// </summary>
public sealed class DesignMcpToolsTests
{
    private readonly IServiceProvider _services;

    public DesignMcpToolsTests()
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<IOntologyCatalog>(_ => OntologyCatalog.BuildFromEmbeddedBase());

        // Register stub registries
        sc.AddSingleton<IAgentRegistry>(new StubAgentRegistry(
        [
            new AgentManifest(
                "gpt-agent", "1.0",
                new AgentHandlerRef("maf"),
                [new ProtocolBinding("openai")],
                []),
        ]));
        sc.AddSingleton<IAgentGraphRegistry>(new StubAgentGraphRegistry([]));
        sc.AddSingleton<IMcpServerRegistry>(new StubMcpServerRegistry([]));
        sc.AddSingleton<ILlmGatewayConfigRegistry>(new StubLlmGatewayConfigRegistry([]));
        sc.AddSingleton<IMcpGatewayConfigRegistry>(new StubMcpGatewayConfigRegistry([]));
        sc.AddSingleton<IContainerPluginRegistry>(new StubContainerPluginRegistry([]));
        sc.AddSingleton<IEvalSuiteRegistry>(new StubEvalSuiteRegistry([]));

        _services = sc.BuildServiceProvider();
    }

    // ── vais.list ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task VaisList_Agent_ReturnsRegisteredManifests()
    {
        var result = await InvokeAsync("vais.list", new { kind = "Agent" });
        result.IsError.Should().BeFalse();
        var doc = JsonDocument.Parse(Content(result));
        doc.RootElement.GetProperty("kind").GetString().Should().Be("Agent");
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("items").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task VaisList_UnknownKind_ReturnsError()
    {
        var result = await InvokeAsync("vais.list", new { kind = "Banana" });
        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task VaisList_MissingKind_ReturnsError()
    {
        var result = await InvokeAsync("vais.list", new { });
        result.IsError.Should().BeTrue();
    }

    // ── kind is case-insensitive ──────────────────────────────────────────────
    // LLM coding agents (the audience) reliably write acronym casings ("LLMGatewayConfig",
    // "MCPServer") or lowercase. The handler must resolve these to the canonical kind rather
    // than reject them (which previously surfaced as a misleading "0 items" to the agent).

    [Theory]
    [InlineData("agent")]
    [InlineData("AGENT")]
    [InlineData("AgEnT")]
    public async Task VaisList_KindIsCaseInsensitive_AndNormalizesOutputKind(string kind)
    {
        var result = await InvokeAsync("vais.list", new { kind });
        result.IsError.Should().BeFalse();
        var doc = JsonDocument.Parse(Content(result));
        doc.RootElement.GetProperty("kind").GetString().Should().Be("Agent");
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
    }

    [Theory]
    [InlineData("LLMGatewayConfig")]
    [InlineData("llmgatewayconfig")]
    public async Task VaisList_AcronymMisCasedKind_IsAcceptedAndNormalized(string kind)
    {
        var result = await InvokeAsync("vais.list", new { kind });
        result.IsError.Should().BeFalse(because: "an acronym-mis-cased kind must resolve to canonical, not error");
        var doc = JsonDocument.Parse(Content(result));
        doc.RootElement.GetProperty("kind").GetString().Should().Be("LlmGatewayConfig");
    }

    [Fact]
    public async Task VaisGet_KindIsCaseInsensitive()
    {
        var result = await InvokeAsync("vais.get", new { kind = "AGENT", name = "gpt-agent" });
        result.IsError.Should().BeFalse();
        var doc = JsonDocument.Parse(Content(result));
        doc.RootElement.GetProperty("metadata").GetProperty("id").GetString().Should().Be("gpt-agent");
    }

    [Fact]
    public async Task VaisDescribe_KindIsCaseInsensitive()
    {
        var result = await InvokeAsync("vais.describe", new { kind = "mcpserver" });
        result.IsError.Should().BeFalse();
    }

    // ── vais.get ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task VaisGet_ExistingAgent_ReturnsEnvelope()
    {
        var result = await InvokeAsync("vais.get", new { kind = "Agent", name = "gpt-agent" });
        result.IsError.Should().BeFalse();
        var doc = JsonDocument.Parse(Content(result));
        doc.RootElement.GetProperty("kind").GetString().Should().Be("Agent");
        doc.RootElement.GetProperty("metadata").GetProperty("id").GetString().Should().Be("gpt-agent");
    }

    [Fact]
    public async Task VaisGet_MissingAgent_ReturnsError()
    {
        var result = await InvokeAsync("vais.get", new { kind = "Agent", name = "no-such-agent" });
        result.IsError.Should().BeTrue();
    }

    // ── vais.describe ─────────────────────────────────────────────────────────

    [Fact]
    public async Task VaisDescribe_Agent_ReturnsOntologyEntry()
    {
        var result = await InvokeAsync("vais.describe", new { kind = "Agent" });
        result.IsError.Should().BeFalse();
        var doc = JsonDocument.Parse(Content(result));
        doc.RootElement.GetProperty("kind").GetString().Should().Be("Agent");
        doc.RootElement.GetProperty("required").GetArrayLength().Should().BeGreaterThan(0);
        doc.RootElement.GetProperty("crossRefs").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task VaisDescribe_UnknownKind_ReturnsError()
    {
        var result = await InvokeAsync("vais.describe", new { kind = "NoSuchKind" });
        result.IsError.Should().BeTrue();
    }

    // ── vais.diff ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task VaisDiff_AgentWithAddedField_ReportsAdded()
    {
        var manifest = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "Agent",
              "metadata": { "id": "gpt-agent", "version": "1.0" },
              "spec": { "handler": "maf", "protocols": ["openai"], "newField": "value" }
            }
            """;
        var result = await InvokeAsync("vais.diff", new { manifest });
        result.IsError.Should().BeFalse();
        var doc = JsonDocument.Parse(Content(result));
        doc.RootElement.GetProperty("added").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task VaisDiff_UnregisteredResource_ReportsNotRegistered()
    {
        var manifest = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "Agent",
              "metadata": { "id": "ghost-agent", "version": "1.0" },
              "spec": { "handler": "maf" }
            }
            """;
        var result = await InvokeAsync("vais.diff", new { manifest });
        result.IsError.Should().BeFalse();
        var doc = JsonDocument.Parse(Content(result));
        doc.RootElement.GetProperty("registered").GetBoolean().Should().BeFalse();
    }

    // ── vais.validate ─────────────────────────────────────────────────────────

    [Fact]
    public async Task VaisValidate_ValidManifest_ReturnsOk()
    {
        var manifest = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "Agent",
              "metadata": { "id": "gpt-agent", "version": "1.0" },
              "spec": { "handler": {"typeName": "maf"}, "protocols": [{"kind": "openai"}] }
            }
            """;
        var result = await InvokeAsync("vais.validate", new { manifest });
        result.IsError.Should().BeFalse();
        var doc = JsonDocument.Parse(Content(result));
        doc.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("errors").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task VaisValidate_DanglingMcpGatewayRef_ReturnsErrorWithSuggestion()
    {
        var manifest = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "Agent",
              "metadata": { "id": "gpt-agent", "version": "1.0" },
              "spec": { "handler": {"typeName": "maf"}, "mcpGatewayRef": "ghost-gateway" }
            }
            """;
        var result = await InvokeAsync("vais.validate", new { manifest });
        result.IsError.Should().BeFalse();
        var doc = JsonDocument.Parse(Content(result));
        doc.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        var errors = doc.RootElement.GetProperty("errors");
        errors.GetArrayLength().Should().BeGreaterThan(0);
        errors.EnumerateArray().Any(e => e.GetString()!.Contains("ghost-gateway")).Should().BeTrue();
        var sugg = doc.RootElement.GetProperty("suggestions");
        sugg.EnumerateArray()
            .Any(s => s.GetString()!.Contains("vais.list") && s.GetString()!.Contains("McpGatewayConfig"))
            .Should().BeTrue();
    }

    [Fact]
    public async Task VaisValidate_MissingEnvelopeKey_ReturnsError()
    {
        var manifest = """{ "kind": "Agent", "metadata": { "id": "x", "version": "1.0" } }""";
        var result = await InvokeAsync("vais.validate", new { manifest });
        result.IsError.Should().BeFalse();
        var doc = JsonDocument.Parse(Content(result));
        doc.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task VaisValidate_ValidManifest_DoesNotChangeRegistrationState()
    {
        var beforeResult = await InvokeAsync("vais.list", new { kind = "Agent" });
        var before = JsonDocument.Parse(Content(beforeResult)).RootElement.GetProperty("count").GetInt32();

        var manifest = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "Agent",
              "metadata": { "id": "gpt-agent", "version": "1.0" },
              "spec": { "handler": {"typeName": "maf"} }
            }
            """;
        await InvokeAsync("vais.validate", new { manifest });

        var afterResult = await InvokeAsync("vais.list", new { kind = "Agent" });
        var after = JsonDocument.Parse(Content(afterResult)).RootElement.GetProperty("count").GetInt32();
        after.Should().Be(before);
    }

    // ── vais-ontology:// resources (ND-8) ────────────────────────────────────

    [Fact]
    public async Task OntologyResources_ListReturnsAllKinds()
    {
        var result = await DesignMcpToolHandlers.ListOntologyResourcesAsync(_services);
        result.Resources.Should().HaveCount(7);
        result.Resources.Select(r => r.Uri).Should().OnlyContain(u => u.StartsWith("vais-ontology://"));
    }

    [Fact]
    public async Task OntologyResources_ReadAgent_ReturnsOntologyJson()
    {
        var result = await DesignMcpToolHandlers.ReadOntologyResourceAsync("vais-ontology://Agent", _services);
        result.Contents.Should().HaveCount(1);
        var text = ((TextResourceContents)result.Contents[0]).Text;
        var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("kind").GetString().Should().Be("Agent");
        doc.RootElement.GetProperty("crossRefs").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public void OntologyResources_ReadUnknownKind_Throws()
    {
        var act = () => DesignMcpToolHandlers.ReadOntologyResourceAsync("vais-ontology://NoSuchKind", _services);
        act.Should().Throw<ArgumentException>().WithMessage("*NoSuchKind*");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<CallToolResult> InvokeAsync(string toolName, object argsObj)
    {
        var argsJson = JsonSerializer.Serialize(argsObj);
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson)!;
        return await DesignMcpToolHandlers.InvokeAsync(toolName, args, _services, default);
    }

    private static string Content(CallToolResult result)
        => ((TextContentBlock)result.Content[0]).Text;

    // ── Stub registries ───────────────────────────────────────────────────────

    private sealed class StubAgentRegistry(IReadOnlyList<AgentManifest> items) : IAgentRegistry
    {
        public async IAsyncEnumerable<AgentManifest> ListAsync(string? labelPrefix = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        { foreach (var m in items) { await Task.CompletedTask; yield return m; } }
        public ValueTask<AgentManifest?> GetAsync(string id, string? version = null, CancellationToken cancellationToken = default)
            => new(items.FirstOrDefault(m => m.Id == id && (version is null || m.Version == version)));
    }

    private sealed class StubAgentGraphRegistry(IReadOnlyList<AgentGraphManifest> items) : IAgentGraphRegistry
    {
        public async IAsyncEnumerable<AgentGraphManifest> ListAsync(string? labelPrefix = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        { foreach (var m in items) { await Task.CompletedTask; yield return m; } }
        public ValueTask<AgentGraphManifest?> GetAsync(string id, string? version = null, CancellationToken cancellationToken = default)
            => new(items.FirstOrDefault(m => m.Id == id));
    }

    private sealed class StubMcpServerRegistry(IReadOnlyList<McpServerManifest> items) : IMcpServerRegistry
    {
        public async IAsyncEnumerable<McpServerManifest> ListAsync(string? labelPrefix = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        { foreach (var m in items) { await Task.CompletedTask; yield return m; } }
        public ValueTask<McpServerManifest?> GetAsync(string id, string? version = null, CancellationToken ct = default)
            => new(items.FirstOrDefault(m => m.Id == id));
        public ValueTask RegisterAsync(McpServerManifest manifest, CancellationToken ct = default) => default;
        public ValueTask RemoveAsync(string id, string version, CancellationToken ct = default) => default;
    }

    private sealed class StubLlmGatewayConfigRegistry(IReadOnlyList<LlmGatewayConfigManifest> items) : ILlmGatewayConfigRegistry
    {
        public async IAsyncEnumerable<LlmGatewayConfigManifest> ListAsync(string? labelPrefix = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        { foreach (var m in items) { await Task.CompletedTask; yield return m; } }
        public ValueTask<LlmGatewayConfigManifest?> GetAsync(string id, string? version = null, CancellationToken ct = default)
            => new(items.FirstOrDefault(m => m.Id == id));
        public ValueTask RegisterAsync(LlmGatewayConfigManifest manifest, CancellationToken ct = default) => default;
        public ValueTask RemoveAsync(string id, string version, CancellationToken ct = default) => default;
    }

    private sealed class StubMcpGatewayConfigRegistry(IReadOnlyList<McpGatewayConfigManifest> items) : IMcpGatewayConfigRegistry
    {
        public async IAsyncEnumerable<McpGatewayConfigManifest> ListAsync(string? labelPrefix = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        { foreach (var m in items) { await Task.CompletedTask; yield return m; } }
        public ValueTask<McpGatewayConfigManifest?> GetAsync(string id, string? version = null, CancellationToken ct = default)
            => new(items.FirstOrDefault(m => m.Id == id));
        public ValueTask RegisterAsync(McpGatewayConfigManifest manifest, CancellationToken ct = default) => default;
        public ValueTask RemoveAsync(string id, string version, CancellationToken ct = default) => default;
    }

    private sealed class StubContainerPluginRegistry(IReadOnlyList<ContainerPluginManifest> items) : IContainerPluginRegistry
    {
        public async IAsyncEnumerable<ContainerPluginManifest> ListAsync(string? labelPrefix = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        { foreach (var m in items) { await Task.CompletedTask; yield return m; } }
        public ValueTask<ContainerPluginManifest?> GetAsync(string id, string? version = null, CancellationToken ct = default)
            => new(items.FirstOrDefault(m => m.Id == id));
        public ValueTask RegisterAsync(ContainerPluginManifest manifest, CancellationToken ct = default) => default;
        public ValueTask RemoveAsync(string id, string version, CancellationToken ct = default) => default;
    }

    private sealed class StubEvalSuiteRegistry(IReadOnlyList<EvalSuiteManifest> items) : IEvalSuiteRegistry
    {
        public async IAsyncEnumerable<EvalSuiteManifest> ListAsync(string? labelPrefix = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        { foreach (var m in items) { await Task.CompletedTask; yield return m; } }
        public ValueTask<EvalSuiteManifest?> GetAsync(string id, string? version = null, CancellationToken ct = default)
            => new(items.FirstOrDefault(m => m.Id == id));
        public ValueTask UpsertAsync(EvalSuiteManifest manifest, CancellationToken ct = default) => default;
        public ValueTask RemoveAsync(string id, string version, CancellationToken ct = default) => default;
    }
}
