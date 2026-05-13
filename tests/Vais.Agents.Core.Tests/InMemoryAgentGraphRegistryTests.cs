// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// Unit tests for <see cref="InMemoryAgentGraphRegistry"/>. Covers CRUD, label-prefix
/// filtering, null-version (latest) resolution, and duplicate/overwrite semantics.
/// </summary>
public sealed class InMemoryAgentGraphRegistryTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static AgentGraphManifest MakeManifest(
        string id,
        string version,
        IReadOnlyDictionary<string, string>? labels = null) =>
        new(
            Id: id,
            Version: version,
            Entry: "start",
            Nodes: new[] { new GraphNode("start", "End") },
            Edges: Array.Empty<GraphEdge>(),
            Labels: labels);

    private static async Task<List<AgentGraphManifest>> CollectAsync(
        IAsyncEnumerable<AgentGraphManifest> source)
    {
        var list = new List<AgentGraphManifest>();
        await foreach (var item in source)
        {
            list.Add(item);
        }
        return list;
    }

    // -------------------------------------------------------------------------
    // 1. ListAsync returns empty on empty registry
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ListAsync_ReturnsEmpty_WhenRegistryIsEmpty()
    {
        var registry = new InMemoryAgentGraphRegistry();

        var results = await CollectAsync(registry.ListAsync());

        results.Should().BeEmpty();
    }

    // -------------------------------------------------------------------------
    // 2. GetAsync returns null for unknown id
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetAsync_ReturnsNull_ForUnknownId()
    {
        var registry = new InMemoryAgentGraphRegistry();

        var result = await registry.GetAsync("no-such-graph");

        result.Should().BeNull();
    }

    // -------------------------------------------------------------------------
    // 3. Register then GetAsync(id, null) returns the manifest (null version = latest)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetAsync_NullVersion_ReturnsManifest_AfterRegister()
    {
        var registry = new InMemoryAgentGraphRegistry();
        var manifest = MakeManifest("my-graph", "1.0");
        registry.Register(manifest);

        var result = await registry.GetAsync("my-graph", version: null);

        result.Should().NotBeNull();
        result!.Id.Should().Be("my-graph");
        result.Version.Should().Be("1.0");
    }

    // -------------------------------------------------------------------------
    // 4. Register then GetAsync(id, version) returns exact version
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetAsync_ExactVersion_ReturnsCorrectManifest()
    {
        var registry = new InMemoryAgentGraphRegistry();
        var v1 = MakeManifest("graph-a", "1.0");
        var v2 = MakeManifest("graph-a", "2.0");
        registry.Register(v1);
        registry.Register(v2);

        var result = await registry.GetAsync("graph-a", "1.0");

        result.Should().NotBeNull();
        result!.Version.Should().Be("1.0");
    }

    // -------------------------------------------------------------------------
    // 5. Multiple versions: GetAsync(id, null) returns latest lexicographic
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetAsync_NullVersion_ReturnsLexicographicLatest_WhenMultipleVersionsExist()
    {
        var registry = new InMemoryAgentGraphRegistry();
        registry.Register(MakeManifest("multi", "1.0"));
        registry.Register(MakeManifest("multi", "2.0"));
        registry.Register(MakeManifest("multi", "1.5"));

        var result = await registry.GetAsync("multi");

        // "2.0" is lexicographically greatest of {"1.0", "2.0", "1.5"}.
        result.Should().NotBeNull();
        result!.Version.Should().Be("2.0");
    }

    // -------------------------------------------------------------------------
    // 6. Register then ListAsync() returns the manifest
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ListAsync_ReturnsRegisteredManifest()
    {
        var registry = new InMemoryAgentGraphRegistry();
        var manifest = MakeManifest("listed-graph", "1.0");
        registry.Register(manifest);

        var results = await CollectAsync(registry.ListAsync());

        results.Should().ContainSingle()
            .Which.Id.Should().Be("listed-graph");
    }

    // -------------------------------------------------------------------------
    // 7. ListAsync(labelPrefix) filters by label prefix
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ListAsync_LabelPrefix_FiltersResults()
    {
        var registry = new InMemoryAgentGraphRegistry();

        registry.Register(MakeManifest("g1", "1.0", labels: new Dictionary<string, string>
        {
            ["team:billing"] = "true"
        }));
        registry.Register(MakeManifest("g2", "1.0", labels: new Dictionary<string, string>
        {
            ["team:sales"] = "true"
        }));
        registry.Register(MakeManifest("g3", "1.0")); // no labels

        var results = await CollectAsync(registry.ListAsync(labelPrefix: "team:billing"));

        results.Should().ContainSingle().Which.Id.Should().Be("g1");
    }

    // -------------------------------------------------------------------------
    // 8. Remove(id, version) removes and GetAsync returns null
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Remove_MakesGetAsyncReturnNull()
    {
        var registry = new InMemoryAgentGraphRegistry();
        registry.Register(MakeManifest("removable", "1.0"));

        var removed = registry.Remove("removable", "1.0");
        var result = await registry.GetAsync("removable", "1.0");

        removed.Should().BeTrue();
        result.Should().BeNull();
    }

    // -------------------------------------------------------------------------
    // 9. Register duplicate (same id+version) overwrites silently
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Register_Duplicate_OverwritesSilently()
    {
        var registry = new InMemoryAgentGraphRegistry();
        registry.Register(MakeManifest("dup", "1.0"));

        var updated = MakeManifest("dup", "1.0", labels: new Dictionary<string, string>
        {
            ["overwritten"] = "yes"
        });
        registry.Register(updated);

        var result = await registry.GetAsync("dup", "1.0");

        result.Should().NotBeNull();
        result!.Labels.Should().ContainKey("overwritten");

        var all = await CollectAsync(registry.ListAsync());
        all.Should().ContainSingle("duplicate registration should not create extra entries");
    }

    // -------------------------------------------------------------------------
    // 10. ListAsync after Remove does not include removed manifest
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ListAsync_DoesNotIncludeRemovedManifest()
    {
        var registry = new InMemoryAgentGraphRegistry();
        registry.Register(MakeManifest("keep", "1.0"));
        registry.Register(MakeManifest("drop", "1.0"));

        registry.Remove("drop", "1.0");

        var results = await CollectAsync(registry.ListAsync());

        results.Should().ContainSingle().Which.Id.Should().Be("keep");
    }
}
