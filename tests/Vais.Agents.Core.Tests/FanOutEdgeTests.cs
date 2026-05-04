// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// FO-7a / FO-7b: Concurrent-edge parsing and validation coverage.
/// </summary>
public sealed class FanOutEdgeTests
{
    // ---- FO-7a: parsing ----

    [Fact]
    public async Task ParseEdge_ConcurrentTrue_SetsFlag()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: AgentGraph
            metadata: { id: test-graph, version: "1.0" }
            spec:
              entry: planner
              nodes:
                - { id: planner,     kind: Agent, ref: { id: planner-agent } }
                - { id: researcher,  kind: Agent, ref: { id: researcher-agent } }
                - { id: analyst,     kind: Agent, ref: { id: analyst-agent } }
                - { id: synthesizer, kind: Agent, ref: { id: synthesizer-agent } }
                - { id: end,         kind: End }
              edges:
                - { from: planner,     to: researcher,  concurrent: true }
                - { from: planner,     to: analyst,     concurrent: true }
                - { from: researcher,  to: synthesizer, concurrent: true }
                - { from: analyst,     to: synthesizer, concurrent: true }
                - { from: synthesizer, to: end }
            """;

        var loader = new YamlAgentGraphManifestLoader();
        var manifests = await loader.LoadFromStringAsync(yaml);
        var manifest = manifests.Should().ContainSingle().Subject;

        manifest.Edges.First(e => e.From == "planner" && e.To == "researcher")
            .Concurrent.Should().BeTrue();
        manifest.Edges.First(e => e.From == "planner" && e.To == "analyst")
            .Concurrent.Should().BeTrue();
        manifest.Edges.First(e => e.From == "researcher" && e.To == "synthesizer")
            .Concurrent.Should().BeTrue();
        manifest.Edges.First(e => e.From == "analyst" && e.To == "synthesizer")
            .Concurrent.Should().BeTrue();
        manifest.Edges.First(e => e.From == "synthesizer" && e.To == "end")
            .Concurrent.Should().BeFalse();
    }

    [Fact]
    public async Task ParseEdge_ConcurrentOmitted_DefaultsFalse()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: AgentGraph
            metadata: { id: sequential, version: "1.0" }
            spec:
              entry: a
              nodes:
                - { id: a, kind: Agent, ref: { id: a-agent } }
                - { id: b, kind: End }
              edges:
                - { from: a, to: b }
            """;

        var loader = new YamlAgentGraphManifestLoader();
        var manifests = await loader.LoadFromStringAsync(yaml);
        manifests[0].Edges.Single().Concurrent.Should().BeFalse();
    }

    // ---- FO-7b: validation ----

    [Fact]
    public void Validate_ConcurrentBranchesOverlappingOutput_AddsError()
    {
        var manifest = BuildManifestWithOverlappingOutputs();
        var errors = new List<string>();
        AgentGraphManifestValidator.Validate(manifest, errors);
        errors.Should().Contain(e => e.Contains("non-overlapping"),
            "overlapping output keys across concurrent branches must be rejected");
    }

    [Fact]
    public void Validate_MixedConcurrentAndNonConcurrentEdgesOnSameNode_AddsError()
    {
        var manifest = BuildManifestWithMixedEdges();
        var errors = new List<string>();
        AgentGraphManifestValidator.Validate(manifest, errors);
        errors.Should().Contain(e => e.Contains("concurrent") && e.Contains("non-concurrent"),
            "a fork node with mixed edge types must be rejected");
    }

    [Fact]
    public void Validate_ValidConcurrentManifest_NoErrors()
    {
        var manifest = BuildValidTwoBranchManifest();
        var errors = new List<string>();
        AgentGraphManifestValidator.Validate(manifest, errors);
        errors.Should().BeEmpty("the two-branch fan-out / fan-in manifest is valid");
    }

    // ---- helpers ----

    private static AgentGraphManifest BuildManifestWithOverlappingOutputs() => new(
        "overlap-test", "1.0", "fork",
        Nodes: new[]
        {
            new GraphNode("fork", "Agent", Ref: new GraphAgentRef("fork-agent")),
            new GraphNode("branch1", "Agent", Ref: new GraphAgentRef("b1"),
                StateBindings: new GraphStateBindings(Output: new[] { "result" })),
            new GraphNode("branch2", "Agent", Ref: new GraphAgentRef("b2"),
                StateBindings: new GraphStateBindings(Output: new[] { "result" })),
            new GraphNode("join", "Agent", Ref: new GraphAgentRef("join-agent")),
            new GraphNode("end", "End"),
        },
        Edges: new[]
        {
            new GraphEdge("fork", "branch1", Concurrent: true),
            new GraphEdge("fork", "branch2", Concurrent: true),
            new GraphEdge("branch1", "join", Concurrent: true),
            new GraphEdge("branch2", "join", Concurrent: true),
            new GraphEdge("join", "end"),
        });

    private static AgentGraphManifest BuildManifestWithMixedEdges() => new(
        "mixed-test", "1.0", "fork",
        Nodes: new[]
        {
            new GraphNode("fork", "Agent", Ref: new GraphAgentRef("fork-agent")),
            new GraphNode("a", "Agent", Ref: new GraphAgentRef("a-agent")),
            new GraphNode("b", "Agent", Ref: new GraphAgentRef("b-agent")),
            new GraphNode("end", "End"),
        },
        Edges: new[]
        {
            new GraphEdge("fork", "a", Concurrent: true),
            new GraphEdge("fork", "b"),              // non-concurrent on same source → error
            new GraphEdge("a", "end"),
            new GraphEdge("b", "end"),
        });

    private static AgentGraphManifest BuildValidTwoBranchManifest() => new(
        "valid-fanout", "1.0", "planner",
        Nodes: new[]
        {
            new GraphNode("planner", "Agent", Ref: new GraphAgentRef("planner-agent")),
            new GraphNode("researcher", "Agent", Ref: new GraphAgentRef("researcher-agent"),
                StateBindings: new GraphStateBindings(Output: new[] { "research_findings" })),
            new GraphNode("analyst", "Agent", Ref: new GraphAgentRef("analyst-agent"),
                StateBindings: new GraphStateBindings(Output: new[] { "analysis" })),
            new GraphNode("synthesizer", "Agent", Ref: new GraphAgentRef("synthesizer-agent"),
                StateBindings: new GraphStateBindings(Output: new[] { "synthesis" })),
            new GraphNode("end", "End"),
        },
        Edges: new[]
        {
            new GraphEdge("planner",    "researcher",  Concurrent: true),
            new GraphEdge("planner",    "analyst",     Concurrent: true),
            new GraphEdge("researcher", "synthesizer", Concurrent: true),
            new GraphEdge("analyst",    "synthesizer", Concurrent: true),
            new GraphEdge("synthesizer", "end"),
        });
}
