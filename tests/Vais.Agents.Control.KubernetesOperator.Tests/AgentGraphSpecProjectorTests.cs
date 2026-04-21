// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents;
using Vais.Agents.Control.Kubernetes;
using Xunit;

namespace Vais.Agents.Control.Kubernetes.Tests;

public sealed class AgentGraphSpecProjectorTests
{
    [Fact]
    public void ToManifest_MapsRequiredFields()
    {
        var spec = new AgentGraphSpec
        {
            GraphId = "pipeline",
            Version = "1.0",
            Entry = "start",
            Nodes = new List<GraphNode> { new("start", "End"), new("finish", "End") },
            Edges = new List<GraphEdge> { new("start", "finish") },
        };

        var manifest = AgentGraphSpecProjector.ToManifest(spec);

        manifest.Id.Should().Be("pipeline");
        manifest.Version.Should().Be("1.0");
        manifest.Entry.Should().Be("start");
        manifest.Nodes.Should().HaveCount(2);
        manifest.Edges.Should().HaveCount(1);
    }

    [Fact]
    public void ToManifest_CopiesOptionalFields()
    {
        var spec = new AgentGraphSpec
        {
            GraphId = "pipeline",
            Version = "1.0",
            Entry = "start",
            Nodes = new List<GraphNode> { new("start", "End") },
            Edges = new List<GraphEdge>(),
            Description = "test graph",
            Labels = new Dictionary<string, string> { ["env"] = "test" },
            Annotations = new Dictionary<string, string> { ["owner"] = "team-a" },
            MaxSteps = 500,
        };

        var manifest = AgentGraphSpecProjector.ToManifest(spec);

        manifest.Description.Should().Be("test graph");
        manifest.Labels.Should().ContainKey("env");
        manifest.Annotations.Should().ContainKey("owner");
        manifest.MaxSteps.Should().Be(500);
    }

    [Fact]
    public void ToManifest_NullLabels_MapsToNull()
    {
        var spec = new AgentGraphSpec
        {
            GraphId = "g",
            Version = "1.0",
            Entry = "start",
            Nodes = new List<GraphNode> { new("start", "End") },
            Edges = new List<GraphEdge>(),
            Labels = null,
        };

        var manifest = AgentGraphSpecProjector.ToManifest(spec);

        manifest.Labels.Should().BeNull();
    }

    [Fact]
    public void ToManifest_CollectionsAreCopied_NotSameReference()
    {
        var node = new GraphNode("start", "End");
        var spec = new AgentGraphSpec
        {
            GraphId = "g",
            Version = "1.0",
            Entry = "start",
            Nodes = new List<GraphNode> { node },
            Edges = new List<GraphEdge>(),
        };

        var manifest = AgentGraphSpecProjector.ToManifest(spec);

        manifest.Nodes.Should().NotBeSameAs(spec.Nodes);
        manifest.Edges.Should().NotBeSameAs(spec.Edges);
    }

    [Fact]
    public void ToManifest_Preserves_RuntimeUrl_On_NodeRef()
    {
        // v0.20 PR 2: runtimeUrl in GraphAgentRef must survive the spec → manifest projection
        // so cross-runtime nodes authored via the K8s CRD work the same as JSON/YAML manifests.
        var spec = new AgentGraphSpec
        {
            GraphId = "cross-rt",
            Version = "1.0",
            Entry = "step",
            Nodes = new List<GraphNode>
            {
                new("step", "Agent", Ref: new GraphAgentRef("remote-agent", "2.0", "https://runtime-b.svc")),
                new("end", "End"),
            },
            Edges = new List<GraphEdge> { new("step", "end") },
        };

        var manifest = AgentGraphSpecProjector.ToManifest(spec);

        var step = manifest.Nodes.Single(n => n.Id == "step");
        step.Ref!.RuntimeUrl.Should().Be("https://runtime-b.svc");
    }
}
