// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents;
using Vais.Agents.Hosting.Orleans;
using Xunit;

namespace Vais.Agents.Hosting.Orleans.Tests;

/// <summary>
/// v0.19 PR 2: Orleans-backed graph registry. Covers register, get (exact version
/// + latest), list, remove, versioning, label-prefix filter, and directory
/// enumeration — all via a live in-memory Orleans TestCluster.
/// </summary>
[Collection(OrleansClusterCollection.CollectionName)]
public sealed class OrleansAgentGraphRegistryTests
{
    private readonly OrleansClusterFixture _fixture;

    public OrleansAgentGraphRegistryTests(OrleansClusterFixture fixture)
    {
        _fixture = fixture;
    }

    private OrleansAgentGraphRegistry BuildRegistry() =>
        new(_fixture.Cluster.Client);

    private static AgentGraphManifest MinimalManifest(string id = "g1", string version = "1.0") =>
        new AgentGraphManifest(
            Id: id,
            Version: version,
            Entry: "start",
            Nodes: new[] { new GraphNode("start", "End") },
            Edges: Array.Empty<GraphEdge>());

    // ── Register + Get ───────────────────────────────────────────────────────

    [Fact]
    public async Task Register_Then_Get_ReturnsManifest()
    {
        var registry = BuildRegistry();
        var id = $"rg-{Guid.NewGuid():N}";
        var manifest = MinimalManifest(id, "1.0");

        await registry.RegisterAsync(manifest);

        var result = await registry.GetAsync(id, "1.0");
        result.Should().NotBeNull();
        result!.Id.Should().Be(id);
        result.Version.Should().Be("1.0");
    }

    [Fact]
    public async Task Get_NullVersion_ReturnsLatestRegistered()
    {
        var registry = BuildRegistry();
        var id = $"rg-{Guid.NewGuid():N}";

        await registry.RegisterAsync(MinimalManifest(id, "1.0"));
        await registry.RegisterAsync(MinimalManifest(id, "2.0"));

        var result = await registry.GetAsync(id, version: null);
        result.Should().NotBeNull();
        result!.Version.Should().Be("2.0");
    }

    [Fact]
    public async Task Get_ExactVersion_ReturnsCorrectVersion()
    {
        var registry = BuildRegistry();
        var id = $"rg-{Guid.NewGuid():N}";

        await registry.RegisterAsync(MinimalManifest(id, "1.0"));
        await registry.RegisterAsync(MinimalManifest(id, "2.0"));

        var result = await registry.GetAsync(id, "1.0");
        result.Should().NotBeNull();
        result!.Version.Should().Be("1.0");
    }

    [Fact]
    public async Task Get_UnknownId_ReturnsNull()
    {
        var registry = BuildRegistry();

        var result = await registry.GetAsync($"does-not-exist-{Guid.NewGuid():N}");
        result.Should().BeNull();
    }

    // ── Remove ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Remove_SpecificVersion_LeavesOtherVersionsIntact()
    {
        var registry = BuildRegistry();
        var id = $"rg-{Guid.NewGuid():N}";

        await registry.RegisterAsync(MinimalManifest(id, "1.0"));
        await registry.RegisterAsync(MinimalManifest(id, "2.0"));

        var removed = await registry.RemoveAsync(id, "1.0");
        removed.Should().BeTrue();

        (await registry.GetAsync(id, "1.0")).Should().BeNull();
        (await registry.GetAsync(id, "2.0")).Should().NotBeNull();
    }

    [Fact]
    public async Task Remove_AllVersions_RemovesFromDirectory()
    {
        var registry = BuildRegistry();
        var id = $"rg-{Guid.NewGuid():N}";

        await registry.RegisterAsync(MinimalManifest(id, "1.0"));
        await registry.RemoveAsync(id, version: null);

        (await registry.GetAsync(id)).Should().BeNull();

        var items = new List<AgentGraphManifest>();
        await foreach (var m in registry.ListAsync()) items.Add(m);
        items.Should().NotContain(m => m.Id == id);
    }

    // ── List + label filter ──────────────────────────────────────────────────

    [Fact]
    public async Task List_AfterRegister_IncludesManifest()
    {
        var registry = BuildRegistry();
        var id = $"rg-{Guid.NewGuid():N}";

        await registry.RegisterAsync(MinimalManifest(id, "1.0"));

        var items = new List<AgentGraphManifest>();
        await foreach (var m in registry.ListAsync()) items.Add(m);

        items.Should().Contain(m => m.Id == id);
    }

    [Fact]
    public async Task List_LabelPrefixFilter_ExcludesNonMatchingManifests()
    {
        var registry = BuildRegistry();
        var idA = $"rg-label-{Guid.NewGuid():N}";
        var idB = $"rg-nolabel-{Guid.NewGuid():N}";

        var withLabel = new AgentGraphManifest(
            Id: idA,
            Version: "1.0",
            Entry: "start",
            Nodes: new[] { new GraphNode("start", "End") },
            Edges: Array.Empty<GraphEdge>(),
            Labels: new Dictionary<string, string> { ["env"] = "test" });

        await registry.RegisterAsync(withLabel);
        await registry.RegisterAsync(MinimalManifest(idB, "1.0"));

        var items = new List<AgentGraphManifest>();
        await foreach (var m in registry.ListAsync("env")) items.Add(m);

        items.Should().Contain(m => m.Id == idA);
        items.Should().NotContain(m => m.Id == idB);
    }

    // ── JSON round-trip ──────────────────────────────────────────────────────

    [Fact]
    public void SerializeDeserialize_RoundTrip_PreservesAllFields()
    {
        var manifest = new AgentGraphManifest(
            Id: "rt-graph",
            Version: "3.0",
            Entry: "classify",
            Nodes: new[]
            {
                new GraphNode("classify", "End"),
                new GraphNode("finish", "End"),
            },
            Edges: new[] { new GraphEdge("classify", "finish") },
            Labels: new Dictionary<string, string> { ["owner"] = "team-a" });

        var json = OrleansAgentGraphRegistry.SerializeManifest(manifest);
        var result = OrleansAgentGraphRegistry.DeserializeManifest(json);

        result.Id.Should().Be("rt-graph");
        result.Version.Should().Be("3.0");
        result.Entry.Should().Be("classify");
        result.Nodes.Should().HaveCount(2);
        result.Edges.Should().HaveCount(1);
        result.Labels.Should().ContainKey("owner");
    }
}
