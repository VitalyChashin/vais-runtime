// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Core.Tests.Ontology;

/// <summary>
/// C1-3 verify: an interceptor written against <see cref="IOntologyBinding"/> works
/// transport-agnostically against both the existing north <see cref="IOntologyCatalog"/>
/// (built over the embedded base ontology) and a fake south <see cref="IDomainOntologyCatalog"/>.
/// </summary>
public sealed class OntologyBindingSeamTests
{
    [Fact]
    public void IOntologyCatalog_ExposesIOntologyBindingSeam()
    {
        var catalog = OntologyCatalog.BuildFromEmbeddedBase();
        var binding = (IOntologyBinding)catalog;

        binding.OntologyVersion.Should().Be(catalog.OntologyVersion);
        binding.ConceptNames.Should().BeEquivalentTo(catalog.Kinds);
    }

    [Fact]
    public void NorthCatalog_ProjectsKindOntologyEntryOntoSeamConceptEntry()
    {
        var catalog = OntologyCatalog.BuildFromEmbeddedBase();
        IOntologyBinding binding = catalog;

        var firstKind = catalog.Kinds[0];
        var fromCatalog = catalog.Get(firstKind);

        binding.TryGetConcept(firstKind, out var fromSeam).Should().BeTrue();
        fromSeam.Name.Should().Be(fromCatalog.Kind);
        fromSeam.Description.Should().Be(fromCatalog.Description);
        fromSeam.Tags.Should().BeEquivalentTo(fromCatalog.Tags);
        fromSeam.CrossRefs.Should().HaveCount(fromCatalog.CrossRefs.Count);
        for (var i = 0; i < fromSeam.CrossRefs.Count; i++)
        {
            fromSeam.CrossRefs[i].FieldPath.Should().Be(fromCatalog.CrossRefs[i].FieldPath);
            fromSeam.CrossRefs[i].TargetConceptName.Should().Be(fromCatalog.CrossRefs[i].TargetKind);
            fromSeam.CrossRefs[i].Cardinality.Should().Be(fromCatalog.CrossRefs[i].Cardinality);
        }
    }

    [Fact]
    public void NorthCatalog_TryGetConceptReturnsFalseForUnknownName()
    {
        var catalog = OntologyCatalog.BuildFromEmbeddedBase();
        IOntologyBinding binding = catalog;

        binding.TryGetConcept("DefinitelyNotAKind__", out var entry).Should().BeFalse();
        entry.Should().BeNull();
    }

    [Fact]
    public void InterceptorWrittenAgainstSeam_WorksAgainstNorthCatalog()
    {
        var catalog = OntologyCatalog.BuildFromEmbeddedBase();
        var firstKind = catalog.Kinds[0];

        var report = ConceptDescriptionReader.Read((IOntologyBinding)catalog, firstKind);

        report.Found.Should().BeTrue();
        report.Description.Should().Be(catalog.Get(firstKind).Description);
    }

    [Fact]
    public void InterceptorWrittenAgainstSeam_WorksAgainstSouthDomainCatalog()
    {
        IDomainOntologyCatalog south = new FakeDomainCatalog("south-v1", new()
        {
            ["fetch_url"] = new OntologyConceptEntry
            {
                Name = "fetch_url",
                Description = "Fetch a URL and return the response body.",
                Tags = ["risk:network", "risk:Destructive"],
                CrossRefs = [new OntologyConceptCrossRef("spec.url", "Url", "one")],
            },
        });

        var report = ConceptDescriptionReader.Read(south, "fetch_url");

        report.Found.Should().BeTrue();
        report.Description.Should().Be("Fetch a URL and return the response body.");
        report.Tags.Should().Contain("risk:Destructive");
        report.CrossRefs.Should().ContainSingle()
            .Which.TargetConceptName.Should().Be("Url");
    }

    [Fact]
    public void InterceptorWrittenAgainstSeam_ReturnsNotFoundForUnknownConceptInEither()
    {
        var north = (IOntologyBinding)OntologyCatalog.BuildFromEmbeddedBase();
        IDomainOntologyCatalog south = new FakeDomainCatalog("south-v1", new());

        ConceptDescriptionReader.Read(north, "Nope__").Found.Should().BeFalse();
        ConceptDescriptionReader.Read(south, "Nope__").Found.Should().BeFalse();
    }

    // ── reusable seam-only reader (stands in for a future ontology interceptor) ──

    private static class ConceptDescriptionReader
    {
        public static ConceptReport Read(IOntologyBinding binding, string conceptName)
            => binding.TryGetConcept(conceptName, out var e)
                ? new ConceptReport(true, e.Description, e.Tags, e.CrossRefs)
                : new ConceptReport(false, null, [], []);
    }

    private sealed record ConceptReport(
        bool Found,
        string? Description,
        IReadOnlyList<string> Tags,
        IReadOnlyList<OntologyConceptCrossRef> CrossRefs);

    private sealed class FakeDomainCatalog(
        string version,
        Dictionary<string, OntologyConceptEntry> concepts) : IDomainOntologyCatalog
    {
        public string OntologyVersion { get; } = version;
        public IReadOnlyList<string> ConceptNames { get; } = [.. concepts.Keys];

        public bool TryGetConcept(string conceptName, out OntologyConceptEntry entry)
        {
            if (concepts.TryGetValue(conceptName, out var e))
            {
                entry = e;
                return true;
            }
            entry = null!;
            return false;
        }
    }
}
