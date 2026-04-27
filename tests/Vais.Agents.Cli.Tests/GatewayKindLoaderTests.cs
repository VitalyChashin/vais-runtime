// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Control;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Cli.Tests;

/// <summary>
/// GCF-19 Phase 2, scenarios 8-9: YAML manifest loader handles all five
/// manifest kinds including the three gateway types added in GCF-13.
/// Tests call the loader directly — same path <c>vais apply -f</c> uses.
/// </summary>
public sealed class GatewayKindLoaderTests
{
    private const string LlmGatewayYaml = """
        apiVersion: vais.agents/v1
        kind: LlmGatewayConfig
        metadata:
          id: llm-gw
          version: "1.0"
        spec:
          middleware: []
        """;

    private const string McpGatewayYaml = """
        apiVersion: vais.agents/v1
        kind: McpGatewayConfig
        metadata:
          id: mcp-gw
          version: "1.0"
        spec:
          middleware: []
        """;

    private const string McpServerYaml = """
        apiVersion: vais.agents/v1
        kind: McpServer
        metadata:
          id: my-server
          version: "1.0"
        spec:
          transport: streamableHttp
          url: http://localhost:9000/mcp
        """;

    private const string AgentYaml = """
        apiVersion: vais.agents/v1
        kind: Agent
        metadata:
          id: chat
          version: "1.0"
        spec:
          handler:
            typeName: Vais.Test.FakeHandler
          protocols: []
          tools: []
        """;

    private const string GraphYaml = """
        apiVersion: vais.agents/v1
        kind: AgentGraph
        metadata:
          id: pipeline
          version: "1.0"
        spec:
          entry: start
          nodes:
            - id: start
              kind: End
          edges: []
        """;

    // ── Scenario 8: each gateway kind parses to the correct ManifestResource case ──

    [Fact]
    public async Task Loader_LlmGatewayConfig_ReturnsLlmGatewayConfigCase()
    {
        var resources = await new YamlAgentGraphManifestLoader().LoadAllResourcesFromStringAsync(LlmGatewayYaml);

        resources.Should().HaveCount(1);
        resources[0].Should().BeOfType<ManifestResource.LlmGatewayConfigCase>();
        ((ManifestResource.LlmGatewayConfigCase)resources[0]).Config.Id.Should().Be("llm-gw");
    }

    [Fact]
    public async Task Loader_McpGatewayConfig_ReturnsMcpGatewayConfigCase()
    {
        var resources = await new YamlAgentGraphManifestLoader().LoadAllResourcesFromStringAsync(McpGatewayYaml);

        resources.Should().HaveCount(1);
        resources[0].Should().BeOfType<ManifestResource.McpGatewayConfigCase>();
        ((ManifestResource.McpGatewayConfigCase)resources[0]).Config.Id.Should().Be("mcp-gw");
    }

    [Fact]
    public async Task Loader_McpServer_ReturnsMcpServerCase()
    {
        var resources = await new YamlAgentGraphManifestLoader().LoadAllResourcesFromStringAsync(McpServerYaml);

        resources.Should().HaveCount(1);
        resources[0].Should().BeOfType<ManifestResource.McpServerCase>();
        var srv = ((ManifestResource.McpServerCase)resources[0]).Server;
        srv.Id.Should().Be("my-server");
        srv.Transport.Should().Be("streamableHttp");
    }

    // ── Scenario 9: mixed five-kind stream preserves order ────────────────────

    [Fact]
    public async Task Loader_AllFiveKinds_ParsesInOrder()
    {
        var mixed = string.Join("\n---\n", LlmGatewayYaml, McpGatewayYaml, McpServerYaml, AgentYaml, GraphYaml);

        var resources = await new YamlAgentGraphManifestLoader().LoadAllResourcesFromStringAsync(mixed);

        resources.Should().HaveCount(5);
        resources[0].Should().BeOfType<ManifestResource.LlmGatewayConfigCase>();
        resources[1].Should().BeOfType<ManifestResource.McpGatewayConfigCase>();
        resources[2].Should().BeOfType<ManifestResource.McpServerCase>();
        resources[3].Should().BeOfType<ManifestResource.AgentCase>();
        resources[4].Should().BeOfType<ManifestResource.AgentGraphCase>();
    }
}
