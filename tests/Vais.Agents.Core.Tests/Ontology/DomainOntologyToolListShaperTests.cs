// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Core.Tests.Ontology;

/// <summary>
/// C1-9 verify gate for the list-time south cartridge. Description rewrite, tag injection,
/// cross-ref injection, hide-tag flagging, passthrough for unknown tools, and per-tool-list
/// cache invalidation on input or ontology-version change.
/// </summary>
public sealed class DomainOntologyToolListShaperTests
{
    private static readonly DomainOntologyArtifact Artifact = new()
    {
        OntologyVersion = "v1",
        Tools = new Dictionary<string, DomainConcept>
        {
            ["fetch_url"] = new()
            {
                Description = "Fetch a URL (cartridge-rewritten).",
                Tags = ["risk:network", "risk:Destructive"],
                CrossRefs = [new DomainCrossRef("url", "Url", "one")],
            },
            ["list_files"] = new() { Tags = ["category:filesystem"] },
        },
    };

    private static IDomainOntologyCatalog NewCatalog(string version = "v1")
        => new DomainOntologyCatalog(
            Artifact with { OntologyVersion = version },
            ["fetch_url", "list_files", "unannotated_tool"]);

    // ── shaping: description rewrite + tag/cross-ref injection ────────────────

    [Fact]
    public void Shape_DescriptionOverrideFromArtifactWinsOverUpstream()
    {
        var shaper = new DomainOntologyToolListShaper();
        var input = new[] { new ToolDescriptor("fetch_url", "Upstream description.") };

        var shaped = shaper.Shape(input, NewCatalog());

        shaped.Should().ContainSingle();
        shaped[0].Description.Should().Be("Fetch a URL (cartridge-rewritten).");
    }

    [Fact]
    public void Shape_MissingOverridePreservesUpstreamDescription()
    {
        var shaper = new DomainOntologyToolListShaper();
        var input = new[] { new ToolDescriptor("list_files", "Upstream description.") };

        var shaped = shaper.Shape(input, NewCatalog());

        shaped[0].Description.Should().Be("Upstream description.");
    }

    [Fact]
    public void Shape_InjectsTagsFromArtifactEntry()
    {
        var shaper = new DomainOntologyToolListShaper();
        var input = new[] { new ToolDescriptor("fetch_url", "x") };

        var shaped = shaper.Shape(input, NewCatalog());

        shaped[0].Tags.Should().BeEquivalentTo(["risk:network", "risk:Destructive"]);
    }

    [Fact]
    public void Shape_InjectsCrossRefsFromArtifactEntry()
    {
        var shaper = new DomainOntologyToolListShaper();
        var input = new[] { new ToolDescriptor("fetch_url", null) };

        var shaped = shaper.Shape(input, NewCatalog());

        shaped[0].CrossRefs.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new OntologyConceptCrossRef("url", "Url", "one"));
    }

    [Fact]
    public void Shape_UnknownToolPassesThrough()
    {
        // "out_of_scope" is NOT in the catalog scope (the projection only knows fetch_url, list_files, unannotated_tool).
        var shaper = new DomainOntologyToolListShaper();
        var input = new[] { new ToolDescriptor("out_of_scope", "Upstream desc.") };

        var shaped = shaper.Shape(input, NewCatalog());

        shaped[0].Should().BeEquivalentTo(
            new ShapedToolDescriptor("out_of_scope", "Upstream desc.", [], [], Hidden: false));
    }

    [Fact]
    public void Shape_ProjectedButUnannotatedToolReturnsEmptyAnnotationsAndIsNotHidden()
    {
        var shaper = new DomainOntologyToolListShaper();
        var input = new[] { new ToolDescriptor("unannotated_tool", "Upstream desc.") };

        var shaped = shaper.Shape(input, NewCatalog());

        shaped[0].Tags.Should().BeEmpty();
        shaped[0].CrossRefs.Should().BeEmpty();
        shaped[0].Hidden.Should().BeFalse();
        shaped[0].Description.Should().Be("Upstream desc.");
    }

    // ── hide-tag flagging ─────────────────────────────────────────────────────

    [Fact]
    public void Shape_DefaultOptionsDoNotHide_AnnotateOnly()
    {
        var shaper = new DomainOntologyToolListShaper();
        var input = new[] { new ToolDescriptor("fetch_url", "x") };

        var shaped = shaper.Shape(input, NewCatalog());

        shaped[0].Hidden.Should().BeFalse(
            "default is annotate-only; deployers opt in to hiding by configuring HideTags");
    }

    [Fact]
    public void Shape_HideTagFlagsMatchingTool()
    {
        var shaper = new DomainOntologyToolListShaper(new DomainOntologyToolListShaperOptions
        {
            HideTags = new HashSet<string>(StringComparer.Ordinal) { "risk:Destructive" },
        });
        var input = new[]
        {
            new ToolDescriptor("fetch_url", "x"),       // has risk:Destructive → hidden
            new ToolDescriptor("list_files", "y"),      // category:filesystem only → not hidden
        };

        var shaped = shaper.Shape(input, NewCatalog());

        shaped.Single(s => s.Name == "fetch_url").Hidden.Should().BeTrue();
        shaped.Single(s => s.Name == "list_files").Hidden.Should().BeFalse();
    }

    // ── cache behavior (success criterion 6) ──────────────────────────────────

    [Fact]
    public void CachedShaper_SecondCallWithSameInputsReturnsCachedInstance()
    {
        var cached = new CachedDomainOntologyToolListShaper();
        var input = new[] { new ToolDescriptor("fetch_url", "x") };
        var catalog = NewCatalog();

        var first = cached.Shape(input, catalog);
        var second = cached.Shape(input, catalog);

        second.Should().BeSameAs(first, "cache hit returns the identical instance");
        cached.Count.Should().Be(1);
    }

    [Fact]
    public void CachedShaper_InvalidatesWhenInputToolListChanges()
    {
        var cached = new CachedDomainOntologyToolListShaper();
        var catalog = NewCatalog();

        var first = cached.Shape(new[] { new ToolDescriptor("fetch_url", "x") }, catalog);
        var second = cached.Shape(new[] { new ToolDescriptor("list_files", "x") }, catalog);

        second.Should().NotBeSameAs(first, "different tool list ⇒ different cache key ⇒ different result");
        cached.Count.Should().Be(2);
    }

    [Fact]
    public void CachedShaper_InvalidatesWhenOntologyVersionChanges()
    {
        var cached = new CachedDomainOntologyToolListShaper();
        var input = new[] { new ToolDescriptor("fetch_url", "x") };

        var first = cached.Shape(input, NewCatalog("v1"));
        var second = cached.Shape(input, NewCatalog("v2"));

        second.Should().NotBeSameAs(first, "different ontology version ⇒ cache miss");
        cached.Count.Should().Be(2);
    }

    [Fact]
    public void CachedShaper_InvalidateClearsAllEntries()
    {
        var cached = new CachedDomainOntologyToolListShaper();
        cached.Shape(new[] { new ToolDescriptor("fetch_url", "x") }, NewCatalog());
        cached.Count.Should().Be(1);

        cached.Invalidate();

        cached.Count.Should().Be(0);
    }

    [Fact]
    public void CachedShaper_CachesDifferentlyOnDescriptionChange()
    {
        var cached = new CachedDomainOntologyToolListShaper();
        var catalog = NewCatalog();

        cached.Shape(new[] { new ToolDescriptor("fetch_url", "desc-a") }, catalog);
        cached.Shape(new[] { new ToolDescriptor("fetch_url", "desc-b") }, catalog);

        cached.Count.Should().Be(2, "tool description is part of the cache key");
    }

    // ── argument guards ──────────────────────────────────────────────────────

    [Fact]
    public void Shape_RejectsNullInputs()
    {
        var shaper = new DomainOntologyToolListShaper();
        FluentActions.Invoking(() => shaper.Shape(null!, NewCatalog())).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => shaper.Shape([], null!)).Should().Throw<ArgumentNullException>();
    }
}
