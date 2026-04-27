// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Vais.Agents.Control;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// GCF-11 — Phase 1 manifest contract and loader tests for gateway config kinds.
/// </summary>
public sealed class GatewayConfigManifestTests
{
    private static readonly JsonSerializerOptions _opts = new(JsonSerializerDefaults.Web);

    // ── 1. LlmGatewayConfigManifest round-trip ────────────────────────────────

    [Fact]
    public void LlmGatewayConfigManifest_RoundTrips_With_All_Optional_Fields()
    {
        var spec = new GatewayMiddlewareSpec("RateLimit",
            JsonDocument.Parse("""{"rpm":100}""").RootElement);

        var manifest = new LlmGatewayConfigManifest(
            Id: "llm-gw-1",
            Version: "1.0",
            Middleware: [spec],
            Description: "Test gateway",
            Labels: new Dictionary<string, string> { ["env"] = "test" })
        {
            RateLimit = new LlmRateLimitSpec { RequestsPerMinute = 60, TokensPerMinute = 10_000 },
            Annotations = new Dictionary<string, string> { ["owner"] = "platform" },
        };

        var json = JsonSerializer.Serialize(manifest, _opts);
        var rt = JsonSerializer.Deserialize<LlmGatewayConfigManifest>(json, _opts)!;

        rt.Id.Should().Be("llm-gw-1");
        rt.Version.Should().Be("1.0");
        rt.Description.Should().Be("Test gateway");
        rt.Labels!["env"].Should().Be("test");
        rt.Middleware.Should().ContainSingle(m => m.Name == "RateLimit");
        rt.RateLimit!.RequestsPerMinute.Should().Be(60);
        rt.RateLimit.TokensPerMinute.Should().Be(10_000);
        rt.Annotations!["owner"].Should().Be("platform");
    }

    [Fact]
    public void LlmGatewayConfigManifest_RoundTrips_With_No_Optional_Fields()
    {
        var manifest = new LlmGatewayConfigManifest(
            Id: "llm-gw-min",
            Version: "2.0",
            Middleware: [new GatewayMiddlewareSpec("Logging")]);

        var json = JsonSerializer.Serialize(manifest, _opts);
        var rt = JsonSerializer.Deserialize<LlmGatewayConfigManifest>(json, _opts)!;

        rt.Id.Should().Be("llm-gw-min");
        rt.Version.Should().Be("2.0");
        rt.Description.Should().BeNull();
        rt.Labels.Should().BeNull();
        rt.RateLimit.Should().BeNull();
        rt.Annotations.Should().BeNull();
        rt.Middleware.Should().ContainSingle(m => m.Name == "Logging");
    }

    // ── 2. McpGatewayConfigManifest with WorkspacePolicies round-trip ─────────

    [Fact]
    public void McpGatewayConfigManifest_RoundTrips_With_WorkspacePolicies()
    {
        var manifest = new McpGatewayConfigManifest(
            Id: "mcp-gw-1",
            Version: "1.0",
            Middleware: [new GatewayMiddlewareSpec("ToolWorkspacePolicy")])
        {
            WorkspacePolicies = new Dictionary<string, McpWorkspacePolicySpec>
            {
                ["ws-alpha"] = new McpWorkspacePolicySpec(
                    AllowedTools: ["search", "read"],
                    DeniedTools: ["delete"],
                    MinPrivilegeLevel: 1),
                ["ws-beta"] = new McpWorkspacePolicySpec(),
            },
        };

        var json = JsonSerializer.Serialize(manifest, _opts);
        var rt = JsonSerializer.Deserialize<McpGatewayConfigManifest>(json, _opts)!;

        rt.Id.Should().Be("mcp-gw-1");
        rt.WorkspacePolicies.Should().HaveCount(2);
        var alpha = rt.WorkspacePolicies!["ws-alpha"];
        alpha.AllowedTools.Should().BeEquivalentTo(["search", "read"]);
        alpha.DeniedTools.Should().BeEquivalentTo(["delete"]);
        alpha.MinPrivilegeLevel.Should().Be(1);
        rt.WorkspacePolicies["ws-beta"].AllowedTools.Should().BeNull();
    }

    // ── 3. McpServerManifest physical + virtual round-trips ──────────────────

    [Fact]
    public void McpServerManifest_Physical_RoundTrips()
    {
        var manifest = new McpServerManifest(
            Id: "my-mcp",
            Version: "1.0",
            Description: "Local stdio server")
        {
            Transport = "stdio",
            Command = "/usr/bin/my-mcp-server",
            Args = ["--verbose"],
            Env = new Dictionary<string, string> { ["API_KEY"] = "secret" },
            Tools = ["search", "summarize"],
            McpGatewayRef = "mcp-gw-1",
        };

        var json = JsonSerializer.Serialize(manifest, _opts);
        var rt = JsonSerializer.Deserialize<McpServerManifest>(json, _opts)!;

        rt.Id.Should().Be("my-mcp");
        rt.Transport.Should().Be("stdio");
        rt.Command.Should().Be("/usr/bin/my-mcp-server");
        rt.Args.Should().BeEquivalentTo(["--verbose"]);
        rt.Env!["API_KEY"].Should().Be("secret");
        rt.Tools.Should().BeEquivalentTo(["search", "summarize"]);
        rt.McpGatewayRef.Should().Be("mcp-gw-1");
        rt.Virtual.Should().BeFalse();
    }

    [Fact]
    public void McpServerManifest_Virtual_With_ToolProjection_RoundTrips()
    {
        var manifest = new McpServerManifest(Id: "virtual-mcp", Version: "1.0")
        {
            Virtual = true,
            Sources =
            [
                new McpServerSourceRef("server-a"),
                new McpServerSourceRef("server-b"),
            ],
            ToolProjection =
            [
                new McpServerToolProjection("search", "server-a"),
                new McpServerToolProjection("summarize", "server-b", "do_summarize"),
            ],
        };

        var json = JsonSerializer.Serialize(manifest, _opts);
        var rt = JsonSerializer.Deserialize<McpServerManifest>(json, _opts)!;

        rt.Virtual.Should().BeTrue();
        rt.Sources.Should().HaveCount(2);
        rt.Sources![0].Ref.Should().Be("server-a");
        rt.ToolProjection.Should().HaveCount(2);
        var proj = rt.ToolProjection![1];
        proj.Name.Should().Be("summarize");
        proj.From.Should().Be("server-b");
        proj.SourceToolName.Should().Be("do_summarize");
    }

    // ── 4. GatewayMiddlewareSpec with nested JsonElement Params ──────────────

    [Fact]
    public void GatewayMiddlewareSpec_Nested_Params_Survives_Serialization()
    {
        var spec = new GatewayMiddlewareSpec("Custom",
            JsonDocument.Parse("""{"timeout":30,"retries":3}""").RootElement);

        var json = JsonSerializer.Serialize(spec, _opts);
        var rt = JsonSerializer.Deserialize<GatewayMiddlewareSpec>(json, _opts)!;

        rt.Name.Should().Be("Custom");
        rt.Params.Should().NotBeNull();
        rt.Params!.Value.GetProperty("timeout").GetInt32().Should().Be(30);
        rt.Params!.Value.GetProperty("retries").GetInt32().Should().Be(3);
    }

    // ── 5. ManifestResource switch exhaustiveness (compile-time) ─────────────

    [Fact]
    public void ManifestResource_Switch_Covers_All_Five_Cases()
    {
        // Verify exhaustive switch compiles and dispatches correctly for all 5 cases.
        var handler = new AgentHandlerRef("SomeHandler");
        var cases = new ManifestResource[]
        {
            new ManifestResource.AgentCase(
                new AgentManifest("a", "1.0", handler, [], [])),
            new ManifestResource.AgentGraphCase(
                new AgentGraphManifest("g", "1.0", "start",
                    Nodes: [new GraphNode("start", "End")],
                    Edges: [])),
            new ManifestResource.LlmGatewayConfigCase(
                new LlmGatewayConfigManifest("l", "1.0", [])),
            new ManifestResource.McpGatewayConfigCase(
                new McpGatewayConfigManifest("m", "1.0", [])),
            new ManifestResource.McpServerCase(
                new McpServerManifest("s", "1.0")),
        };

        var ids = cases.Select(r => r switch
        {
            ManifestResource.AgentCase c => c.Manifest.Id,
            ManifestResource.AgentGraphCase c => c.Graph.Id,
            ManifestResource.LlmGatewayConfigCase c => c.Config.Id,
            ManifestResource.McpGatewayConfigCase c => c.Config.Id,
            ManifestResource.McpServerCase c => c.Server.Id,
            _ => throw new InvalidOperationException("unreachable"),
        }).ToList();

        ids.Should().BeEquivalentTo(["a", "g", "l", "m", "s"]);
    }

    // ── 6. JSON loader dispatches LlmGatewayConfig kind ──────────────────────

    [Fact]
    public async Task JsonLoader_Parses_LlmGatewayConfig_Kind()
    {
        var json = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "LlmGatewayConfig",
              "metadata": { "id": "llm-gw", "version": "1.0", "description": "Test" },
              "spec": {
                "middleware": [
                  { "name": "RateLimit", "params": { "rpm": 50 } }
                ],
                "rateLimit": { "requestsPerMinute": 50 }
              }
            }
            """;

        var loader = new JsonAgentGraphManifestLoader();
        var resources = await loader.LoadAllResourcesFromStringAsync(json);

        resources.Should().ContainSingle();
        var c = resources[0].Should().BeOfType<ManifestResource.LlmGatewayConfigCase>().Subject;
        c.Config.Id.Should().Be("llm-gw");
        c.Config.Description.Should().Be("Test");
        c.Config.Middleware.Should().ContainSingle(m => m.Name == "RateLimit");
        c.Config.RateLimit!.RequestsPerMinute.Should().Be(50);
    }

    [Fact]
    public async Task JsonLoader_Parses_McpGatewayConfig_Kind()
    {
        var json = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "McpGatewayConfig",
              "metadata": { "id": "mcp-gw", "version": "1.0" },
              "spec": {
                "middleware": [{ "name": "ToolWorkspacePolicy" }],
                "workspacePolicies": {
                  "ws-1": { "allowedTools": ["search"], "minPrivilegeLevel": 2 }
                }
              }
            }
            """;

        var loader = new JsonAgentGraphManifestLoader();
        var resources = await loader.LoadAllResourcesFromStringAsync(json);

        resources.Should().ContainSingle();
        var c = resources[0].Should().BeOfType<ManifestResource.McpGatewayConfigCase>().Subject;
        c.Config.Id.Should().Be("mcp-gw");
        c.Config.WorkspacePolicies!["ws-1"].AllowedTools.Should().BeEquivalentTo(["search"]);
        c.Config.WorkspacePolicies["ws-1"].MinPrivilegeLevel.Should().Be(2);
    }

    [Fact]
    public async Task JsonLoader_Parses_McpServer_Physical_Kind()
    {
        var json = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "McpServer",
              "metadata": { "id": "stdio-srv", "version": "1.0" },
              "spec": {
                "transport": "stdio",
                "command": "/usr/bin/srv",
                "args": ["--port", "8080"],
                "tools": ["tool-a"]
              }
            }
            """;

        var loader = new JsonAgentGraphManifestLoader();
        var resources = await loader.LoadAllResourcesFromStringAsync(json);

        resources.Should().ContainSingle();
        var c = resources[0].Should().BeOfType<ManifestResource.McpServerCase>().Subject;
        c.Server.Id.Should().Be("stdio-srv");
        c.Server.Transport.Should().Be("stdio");
        c.Server.Command.Should().Be("/usr/bin/srv");
        c.Server.Args.Should().BeEquivalentTo(["--port", "8080"]);
        c.Server.Tools.Should().BeEquivalentTo(["tool-a"]);
    }

    [Fact]
    public async Task JsonLoader_UnknownKind_Throws_ValidationException()
    {
        var json = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "SomeUnknownKind",
              "metadata": { "id": "x", "version": "1.0" },
              "spec": {}
            }
            """;

        var loader = new JsonAgentGraphManifestLoader();
        await FluentActions.Invoking(async () => await loader.LoadAllResourcesFromStringAsync(json))
            .Should().ThrowAsync<AgentManifestValidationException>()
            .WithMessage("*SomeUnknownKind*");
    }

    // ── 7. YAML loader dispatches new kinds ───────────────────────────────────

    [Fact]
    public async Task YamlLoader_Parses_LlmGatewayConfig_Kind()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: LlmGatewayConfig
            metadata:
              id: llm-gw-yaml
              version: "1.0"
            spec:
              middleware:
                - name: Logging
              rateLimit:
                tokensPerMinute: 5000
            """;

        var loader = new YamlAgentGraphManifestLoader();
        var resources = await loader.LoadAllResourcesFromStringAsync(yaml);

        resources.Should().ContainSingle();
        var c = resources[0].Should().BeOfType<ManifestResource.LlmGatewayConfigCase>().Subject;
        c.Config.Id.Should().Be("llm-gw-yaml");
        c.Config.RateLimit!.TokensPerMinute.Should().Be(5000);
    }

    [Fact]
    public async Task YamlLoader_Parses_McpServer_Virtual_Kind()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: McpServer
            metadata:
              id: virt-srv
              version: "1.0"
            spec:
              virtual: true
              sources:
                - ref: server-a
                - ref: server-b
              toolProjection:
                - name: search
                  from: server-a
                - name: summarize
                  from: server-b
                  sourceToolName: do_summarize
            """;

        var loader = new YamlAgentGraphManifestLoader();
        var resources = await loader.LoadAllResourcesFromStringAsync(yaml);

        resources.Should().ContainSingle();
        var c = resources[0].Should().BeOfType<ManifestResource.McpServerCase>().Subject;
        c.Server.Id.Should().Be("virt-srv");
        c.Server.Virtual.Should().BeTrue();
        c.Server.Sources.Should().HaveCount(2);
        c.Server.ToolProjection![1].SourceToolName.Should().Be("do_summarize");
    }

    // ── 8. Mixed YAML (all five kinds, ---separated) → five resources ─────────

    [Fact]
    public async Task Mixed_Yaml_AllFiveKinds_ReturnsInOrder()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: Agent
            metadata: { id: agent-1, version: "1.0" }
            spec:
              systemPrompt: { inline: "Hi" }
            ---
            apiVersion: vais.agents/v1
            kind: AgentGraph
            metadata: { id: graph-1, version: "1.0" }
            spec:
              entry: n
              nodes:
                - id: n
                  kind: End
              edges: []
            ---
            apiVersion: vais.agents/v1
            kind: LlmGatewayConfig
            metadata: { id: llm-1, version: "1.0" }
            spec:
              middleware:
                - name: Logging
            ---
            apiVersion: vais.agents/v1
            kind: McpGatewayConfig
            metadata: { id: mcp-gw-1, version: "1.0" }
            spec:
              middleware:
                - name: ToolWorkspacePolicy
            ---
            apiVersion: vais.agents/v1
            kind: McpServer
            metadata: { id: srv-1, version: "1.0" }
            spec:
              transport: streamableHttp
              url: https://mcp.example.com/sse
            """;

        var loader = new YamlAgentGraphManifestLoader();
        var resources = await loader.LoadAllResourcesFromStringAsync(yaml);

        resources.Should().HaveCount(5);
        resources[0].Should().BeOfType<ManifestResource.AgentCase>()
            .Which.Manifest.Id.Should().Be("agent-1");
        resources[1].Should().BeOfType<ManifestResource.AgentGraphCase>()
            .Which.Graph.Id.Should().Be("graph-1");
        resources[2].Should().BeOfType<ManifestResource.LlmGatewayConfigCase>()
            .Which.Config.Id.Should().Be("llm-1");
        resources[3].Should().BeOfType<ManifestResource.McpGatewayConfigCase>()
            .Which.Config.Id.Should().Be("mcp-gw-1");
        resources[4].Should().BeOfType<ManifestResource.McpServerCase>()
            .Which.Server.Id.Should().Be("srv-1");
    }

    // ── 9. AgentManifest with gateway refs round-trips ────────────────────────

    [Fact]
    public void AgentManifest_GatewayRefs_RoundTrip()
    {
        var manifest = new AgentManifest(
            "my-agent", "1.0", new AgentHandlerRef("Handler"), [], [])
        {
            LlmGatewayRef = "llm-gw-1",
            McpGatewayRef = "mcp-gw-1",
        };

        var json = JsonSerializer.Serialize(manifest, _opts);
        var rt = JsonSerializer.Deserialize<AgentManifest>(json, _opts)!;

        rt.LlmGatewayRef.Should().Be("llm-gw-1");
        rt.McpGatewayRef.Should().Be("mcp-gw-1");
    }

    // ── 10. McpServerRef registered transport round-trips ─────────────────────

    [Fact]
    public void McpServerRef_RegisteredTransport_Constant_And_RoundTrip()
    {
        McpServerRef.RegisteredTransport.Should().Be("registered");

        var serverRef = new McpServerRef("my-registered-server", McpServerRef.RegisteredTransport);

        var json = JsonSerializer.Serialize(serverRef, _opts);
        var rt = JsonSerializer.Deserialize<McpServerRef>(json, _opts)!;

        rt.Name.Should().Be("my-registered-server");
        rt.Transport.Should().Be(McpServerRef.RegisteredTransport);
    }
}
