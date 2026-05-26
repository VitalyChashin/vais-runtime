// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Core.Tests.Ontology;

/// <summary>
/// C1-8 verify gate: <see cref="DomainOntologyCatalog"/> merges the
/// <see cref="DomainOntologyArtifact"/> over the projected tool scope. Unknown tool =
/// passthrough (TryGetConcept returns false). Implements <see cref="IDomainOntologyCatalog"/>
/// so an interceptor written against <see cref="IOntologyBinding"/> works against it.
/// </summary>
public sealed class DomainOntologyCatalogTests
{
    private static readonly DomainOntologyArtifact SampleArtifact = new()
    {
        OntologyVersion = "v3",
        Tools = new Dictionary<string, DomainConcept>
        {
            ["fetch_url"] = new()
            {
                Description = "Fetch a URL and return the response body.",
                Tags = ["risk:network", "risk:Destructive"],
                CrossRefs = [new DomainCrossRef("url", "Url", "one")],
            },
            ["list_files"] = new() { Tags = ["category:filesystem"] },
        },
    };

    // ── projection-scoped mode (virtual server with explicit projection) ──────

    [Fact]
    public void Catalog_ScopedToProjection_ConceptNamesEqualProjection()
    {
        var catalog = new DomainOntologyCatalog(SampleArtifact, ["fetch_url", "list_files", "unannotated_tool"]);

        catalog.ConceptNames.Should().BeEquivalentTo(["fetch_url", "list_files", "unannotated_tool"]);
        catalog.OntologyVersion.Should().Be("v3");
    }

    [Fact]
    public void Catalog_MergesArtifactAnnotationsOverProjection()
    {
        var catalog = new DomainOntologyCatalog(SampleArtifact, ["fetch_url", "list_files"]);

        catalog.TryGetConcept("fetch_url", out var fetch).Should().BeTrue();
        fetch.Name.Should().Be("fetch_url");
        fetch.Description.Should().Be("Fetch a URL and return the response body.");
        fetch.Tags.Should().BeEquivalentTo(["risk:network", "risk:Destructive"]);
        fetch.CrossRefs.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new OntologyConceptCrossRef("url", "Url", "one"));

        catalog.TryGetConcept("list_files", out var list).Should().BeTrue();
        list.Tags.Should().ContainSingle().Which.Should().Be("category:filesystem");
        list.Description.Should().BeNull("artifact entry has no description override");
        list.CrossRefs.Should().BeEmpty();
    }

    [Fact]
    public void Catalog_ProjectedToolWithoutArtifactEntry_ReturnsEmptyConcept()
    {
        var catalog = new DomainOntologyCatalog(SampleArtifact, ["fetch_url", "unannotated_tool"]);

        catalog.TryGetConcept("unannotated_tool", out var entry).Should().BeTrue(
            "the tool is in the projection scope — concept exists with empty annotations");
        entry.Name.Should().Be("unannotated_tool");
        entry.Description.Should().BeNull();
        entry.Tags.Should().BeEmpty();
        entry.CrossRefs.Should().BeEmpty();
    }

    [Fact]
    public void Catalog_UnknownTool_ReturnsFalse_Passthrough()
    {
        var catalog = new DomainOntologyCatalog(SampleArtifact, ["fetch_url"]);

        catalog.TryGetConcept("not_in_scope", out var entry).Should().BeFalse(
            "outside the projection scope = passthrough; no cartridge shaping applied");
        entry.Should().BeNull();
    }

    // ── artifact-only mode (no projection supplied) ───────────────────────────

    [Fact]
    public void Catalog_NoProjection_ScopeIsArtifactToolNames()
    {
        var catalog = new DomainOntologyCatalog(SampleArtifact);

        catalog.ConceptNames.Should().BeEquivalentTo(["fetch_url", "list_files"]);
        catalog.TryGetConcept("fetch_url", out _).Should().BeTrue();
        catalog.TryGetConcept("list_files", out _).Should().BeTrue();
        catalog.TryGetConcept("not_annotated", out _).Should().BeFalse();
    }

    [Fact]
    public void Catalog_EmptyArtifactAndNoProjection_HasNoConcepts()
    {
        var catalog = new DomainOntologyCatalog(DomainOntologyArtifact.Empty);

        catalog.ConceptNames.Should().BeEmpty();
        catalog.TryGetConcept("anything", out _).Should().BeFalse();
    }

    // ── seam compatibility ────────────────────────────────────────────────────

    [Fact]
    public void Catalog_SatisfiesIOntologyBindingSeam()
    {
        IOntologyBinding binding = new DomainOntologyCatalog(SampleArtifact);

        binding.OntologyVersion.Should().Be("v3");
        binding.ConceptNames.Should().Contain("fetch_url");
        binding.TryGetConcept("fetch_url", out var entry).Should().BeTrue();
        entry.Tags.Should().Contain("risk:Destructive");
    }

    [Fact]
    public void Catalog_IsAlsoIDomainOntologyCatalog()
    {
        IDomainOntologyCatalog south = new DomainOntologyCatalog(SampleArtifact, ["fetch_url"]);

        south.ConceptNames.Should().ContainSingle().Which.Should().Be("fetch_url");
    }

    // ── argument guards ──────────────────────────────────────────────────────

    [Fact]
    public void Constructor_RejectsNullArtifact()
    {
        FluentActions.Invoking(() => new DomainOntologyCatalog(null!))
            .Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void TryGetConcept_RejectsNullOrWhitespaceName()
    {
        var catalog = new DomainOntologyCatalog(SampleArtifact);
        FluentActions.Invoking(() => catalog.TryGetConcept(null!, out _)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => catalog.TryGetConcept("", out _)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => catalog.TryGetConcept(" ", out _)).Should().Throw<ArgumentException>();
    }
}
