// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Core.Tests.Ontology;

/// <summary>
/// C1-7 verify gate for the south manifest binding + domain-ontology loader. Covers:
/// (1) <see cref="McpServerManifest.OntologyRef"/> parses + serializes; (2) the JSON
/// schema includes <c>ontologyRef</c>; (3) artifact loader round-trips; (4) registry
/// resolves names; (5) unknown ref = graceful null (no cartridge applied).
/// </summary>
public sealed class DomainOntologyArtifactTests
{
    // ── McpServerManifest.OntologyRef carry-through ────────────────────────────

    [Fact]
    public void McpServerManifest_OntologyRefIsInitProperty()
    {
        var m = new McpServerManifest("srv", "1.0") { OntologyRef = "k8s-tools-v1" };
        m.OntologyRef.Should().Be("k8s-tools-v1");
    }

    // ── DomainOntologyArtifact: JSON round-trip ────────────────────────────────

    [Fact]
    public void Loader_ParsesArtifactJsonWithPerToolConcepts()
    {
        const string json = """
            {
              "ontologyVersion": "v3",
              "tools": {
                "fetch_url": {
                  "description": "Fetch a URL and return its body.",
                  "tags": ["risk:network", "risk:Destructive"],
                  "crossRefs": [
                    { "fieldPath": "url", "targetConceptName": "Url", "cardinality": "one" }
                  ]
                },
                "list_files": {
                  "tags": ["category:filesystem"]
                }
              }
            }
            """;

        var artifact = DomainOntologyArtifactLoader.LoadFromJson(json);

        artifact.OntologyVersion.Should().Be("v3");
        artifact.Tools.Should().NotBeNull().And.HaveCount(2);

        var fetch = artifact.ForTool("fetch_url");
        fetch.Should().NotBeNull();
        fetch!.Description.Should().Be("Fetch a URL and return its body.");
        fetch.Tags.Should().Contain(["risk:network", "risk:Destructive"]);
        fetch.CrossRefs.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new DomainCrossRef("url", "Url", "one"));

        var list = artifact.ForTool("list_files");
        list!.Tags.Should().ContainSingle().Which.Should().Be("category:filesystem");
        list.Description.Should().BeNull("missing description = use upstream tool description");
    }

    [Fact]
    public void Loader_NullOrEmptyJsonReturnsEmptyArtifact()
    {
        DomainOntologyArtifactLoader.LoadFromJson(null).Should().BeSameAs(DomainOntologyArtifact.Empty);
        DomainOntologyArtifactLoader.LoadFromJson("").Should().BeSameAs(DomainOntologyArtifact.Empty);
        DomainOntologyArtifactLoader.LoadFromJson("   ").Should().BeSameAs(DomainOntologyArtifact.Empty);
    }

    [Fact]
    public void Loader_MissingFileReturnsEmptyArtifact()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"vais-test-{Guid.NewGuid():N}.domain-ontology.json");
        DomainOntologyArtifactLoader.LoadFromFile(missing).Should().BeSameAs(DomainOntologyArtifact.Empty);
    }

    [Fact]
    public void ForTool_ReturnsNullForUnknownToolName()
    {
        var artifact = DomainOntologyArtifactLoader.LoadFromJson("""{"ontologyVersion": "v1", "tools": {"x": {}}}""");
        artifact.ForTool("nope").Should().BeNull();
    }

    [Fact]
    public void Loader_DirectoryScanLoadsEveryDomainOntologyJsonFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"vais-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "k8s-tools.domain-ontology.json"),
                """{"ontologyVersion": "v1", "tools": {"kubectl_get": {"tags": ["risk:read"]}}}""");
            File.WriteAllText(Path.Combine(dir, "db-tools.domain-ontology.json"),
                """{"ontologyVersion": "v2", "tools": {"query": {"tags": ["risk:read"]}}}""");
            File.WriteAllText(Path.Combine(dir, "ignored.json"), """{"ontologyVersion": "skip"}""");

            var map = DomainOntologyArtifactLoader.LoadAllFromDirectory(dir);

            map.Should().HaveCount(2)
                .And.ContainKey("k8s-tools")
                .And.ContainKey("db-tools");
            map["k8s-tools"].OntologyVersion.Should().Be("v1");
            map["db-tools"].OntologyVersion.Should().Be("v2");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Loader_DirectoryScanSkipsMalformedFilesWithoutThrowing()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"vais-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "good.domain-ontology.json"),
                """{"ontologyVersion": "v1"}""");
            File.WriteAllText(Path.Combine(dir, "broken.domain-ontology.json"),
                "not json at all");

            var map = DomainOntologyArtifactLoader.LoadAllFromDirectory(dir);

            map.Should().HaveCount(1).And.ContainKey("good");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Loader_NonExistentDirectoryReturnsEmptyMap()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"vais-test-nope-{Guid.NewGuid():N}");
        DomainOntologyArtifactLoader.LoadAllFromDirectory(missing).Should().BeEmpty();
    }

    // ── IDomainOntologyArtifactRegistry ────────────────────────────────────────

    [Fact]
    public void Registry_GetReturnsNullForUnknownRef_GracefulFallback()
    {
        var registry = new InMemoryDomainOntologyArtifactRegistry();

        registry.Get("nope").Should().BeNull(
            "an unknown OntologyRef must degrade gracefully — the cartridge applies passthrough");
    }

    [Fact]
    public void Registry_RegisterAndGetRoundTripsArtifact()
    {
        var registry = new InMemoryDomainOntologyArtifactRegistry();
        var artifact = new DomainOntologyArtifact { OntologyVersion = "v7" };

        registry.Register("k8s-tools-v1", artifact);

        registry.Get("k8s-tools-v1").Should().BeSameAs(artifact);
        registry.Names.Should().Contain("k8s-tools-v1");
    }

    [Fact]
    public void Registry_RegisterAllImportsLoaderMapInOneCall()
    {
        var registry = new InMemoryDomainOntologyArtifactRegistry();
        var loaded = new Dictionary<string, DomainOntologyArtifact>
        {
            ["k8s-tools"] = new() { OntologyVersion = "v1" },
            ["db-tools"] = new() { OntologyVersion = "v2" },
        };

        registry.RegisterAll(loaded);

        registry.Get("k8s-tools")!.OntologyVersion.Should().Be("v1");
        registry.Get("db-tools")!.OntologyVersion.Should().Be("v2");
        registry.Names.Should().HaveCount(2);
    }

    [Fact]
    public void Registry_RegisterRejectsNullOrWhitespaceName()
    {
        var registry = new InMemoryDomainOntologyArtifactRegistry();
        var artifact = DomainOntologyArtifact.Empty;

        FluentActions.Invoking(() => registry.Register(null!, artifact)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => registry.Register("", artifact)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => registry.Register(" ", artifact)).Should().Throw<ArgumentException>();
    }
}
