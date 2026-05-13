// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Control;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Cli.Tests;

/// <summary>
/// Tests the multi-kind YAML loading path that <c>vais apply</c> routes through.
/// Tests the loaders directly rather than the full CLI command stack, keeping the
/// tests fast and noise-free.
/// </summary>
public sealed class ApplyCommandGraphDispatchTests
{
    private const string AgentYaml = """
        apiVersion: vais.agents/v1
        kind: Agent
        metadata:
          name: chat-agent
          id: chat
          version: "1.0"
        spec:
          handler:
            typeName: Vais.Agents.Samples.ChatAgent
          protocols:
            - kind: Http
          tools: []
        """;

    private const string GraphYaml = """
        apiVersion: vais.agents/v1
        kind: AgentGraph
        metadata:
          name: my-pipeline
          id: pipeline
          version: "1.0"
        spec:
          entry: start
          nodes:
            - id: start
              kind: End
          edges: []
        """;

    [Fact]
    public async Task Loader_AgentOnly_ReturnsAgentCase()
    {
        var resources = await new YamlAgentGraphManifestLoader().LoadAllResourcesFromStringAsync(AgentYaml);

        resources.Should().HaveCount(1);
        resources[0].Should().BeOfType<ManifestResource.AgentCase>();
        ((ManifestResource.AgentCase)resources[0]).Manifest.Id.Should().Be("chat");
    }

    [Fact]
    public async Task Loader_GraphOnly_ReturnsGraphCase()
    {
        var resources = await new YamlAgentGraphManifestLoader().LoadAllResourcesFromStringAsync(GraphYaml);

        resources.Should().HaveCount(1);
        resources[0].Should().BeOfType<ManifestResource.AgentGraphCase>();
        ((ManifestResource.AgentGraphCase)resources[0]).Graph.Id.Should().Be("pipeline");
    }

    [Fact]
    public async Task Loader_MixedKindYaml_ReturnsBothCases()
    {
        var mixed = AgentYaml + "\n---\n" + GraphYaml;

        var resources = await new YamlAgentGraphManifestLoader().LoadAllResourcesFromStringAsync(mixed);

        resources.Should().HaveCount(2);
        resources.OfType<ManifestResource.AgentCase>().Should().HaveCount(1);
        resources.OfType<ManifestResource.AgentGraphCase>().Should().HaveCount(1);
    }

    [Fact]
    public async Task Loader_MultipleGraphs_ReturnsAllGraphCases()
    {
        var twoGraphs = GraphYaml + "\n---\n" + GraphYaml.Replace("pipeline", "pipeline2");

        var resources = await new YamlAgentGraphManifestLoader().LoadAllResourcesFromStringAsync(twoGraphs);

        resources.Should().HaveCount(2);
        resources.Should().AllBeOfType<ManifestResource.AgentGraphCase>();
    }

    [Fact]
    public async Task Loader_EmptyContent_ReturnsEmpty()
    {
        var resources = await new YamlAgentGraphManifestLoader().LoadAllResourcesFromStringAsync(string.Empty);

        resources.Should().BeEmpty();
    }

    [Fact]
    public async Task Loader_GraphYaml_FieldsRoundTrip()
    {
        var resources = await new YamlAgentGraphManifestLoader().LoadAllResourcesFromStringAsync(GraphYaml);

        var graph = ((ManifestResource.AgentGraphCase)resources[0]).Graph;
        graph.Id.Should().Be("pipeline");
        graph.Version.Should().Be("1.0");
        graph.Entry.Should().Be("start");
        graph.Nodes.Should().HaveCount(1);
        graph.Edges.Should().BeEmpty();
    }
}
