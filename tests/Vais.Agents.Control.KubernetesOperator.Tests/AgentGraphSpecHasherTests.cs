// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents;
using Vais.Agents.Control.Kubernetes;
using Xunit;

namespace Vais.Agents.Control.Kubernetes.Tests;

public sealed class AgentGraphSpecHasherTests
{
    private static AgentGraphSpec MinimalSpec(string graphId = "g1", string version = "1.0") => new()
    {
        GraphId = graphId,
        Version = version,
        Entry = "start",
        Nodes = new List<GraphNode> { new("start", "End") },
        Edges = new List<GraphEdge>(),
    };

    [Fact]
    public void Compute_ReturnsSha256Prefix()
    {
        var hash = AgentGraphSpecHasher.Compute(MinimalSpec());
        hash.Should().StartWith("sha256:");
    }

    [Fact]
    public void Compute_SameSpec_ProducesSameHash()
    {
        var hash1 = AgentGraphSpecHasher.Compute(MinimalSpec());
        var hash2 = AgentGraphSpecHasher.Compute(MinimalSpec());
        hash1.Should().Be(hash2);
    }

    [Fact]
    public void Compute_DifferentVersion_ProducesDifferentHash()
    {
        var hash1 = AgentGraphSpecHasher.Compute(MinimalSpec(version: "1.0"));
        var hash2 = AgentGraphSpecHasher.Compute(MinimalSpec(version: "2.0"));
        hash1.Should().NotBe(hash2);
    }
}
