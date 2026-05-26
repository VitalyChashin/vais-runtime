// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// D-13 verify gate: <see cref="JsonOntologyOverlayWriter"/> merges an approved
/// <see cref="RecipeProposal"/> into the on-disk overlay JSON. Merge is idempotent,
/// concurrent writes serialize on a per-path semaphore, and writes are atomic
/// (temp-file + rename).
/// </summary>
public sealed class OntologyOverlayWriterTests
{
    [Fact]
    public void Merge_WorkflowRecipe_AppendsRecipeEntry_WithStableName()
    {
        var existing = OntologyOverlay.Empty;
        var p = Proposal(RecipeProposalKind.WorkflowRecipe, concept: "fetch", body: "fetch -> summarize");

        var merged = JsonOntologyOverlayWriter.Merge(existing, p);

        merged.Recipes.Should().HaveCount(1);
        merged.Recipes![0].Steps.Should().HaveCount(2);
        merged.Recipes[0].Steps[0].Kind.Should().Be("fetch");
        merged.Recipes[0].Steps[1].Kind.Should().Be("summarize");
        merged.Recipes[0].Name.Should().Contain(p.ProposalId[..8]);
    }

    [Fact]
    public void Merge_WorkflowRecipe_IsIdempotent_NoDuplicate()
    {
        var existing = OntologyOverlay.Empty;
        var p = Proposal(RecipeProposalKind.WorkflowRecipe, concept: "fetch", body: "fetch -> summarize");

        var first = JsonOntologyOverlayWriter.Merge(existing, p);
        var second = JsonOntologyOverlayWriter.Merge(first, p);

        second.Should().BeSameAs(first, "no-op merge returns the same reference");
        second.Recipes.Should().HaveCount(1);
    }

    [Fact]
    public void Merge_TagSuggestion_AppendsTagToKind_Idempotent()
    {
        var existing = OntologyOverlay.Empty;
        var p = Proposal(RecipeProposalKind.TagSuggestion, concept: "ContainerPlugin", body: "risk:Destructive");

        var merged = JsonOntologyOverlayWriter.Merge(existing, p);

        merged.Kinds!["ContainerPlugin"].Tags.Should().Equal("risk:Destructive");

        var again = JsonOntologyOverlayWriter.Merge(merged, p);
        again.Should().BeSameAs(merged, "duplicate tag is a no-op");
    }

    [Fact]
    public void Merge_DescriptionRewrite_SetsKindDescription_NoOpOnMatch()
    {
        var p = Proposal(RecipeProposalKind.DescriptionRewrite, concept: "Agent", body: "Stateful conversational agent backed by an Orleans grain.");

        var merged = JsonOntologyOverlayWriter.Merge(OntologyOverlay.Empty, p);
        merged.Kinds!["Agent"].Description.Should().Be(p.Body);

        var again = JsonOntologyOverlayWriter.Merge(merged, p);
        again.Should().BeSameAs(merged);
    }

    [Fact]
    public async Task MergeAsync_WritesFileAtomically_ReportsChange()
    {
        var path = NewTempPath();
        var writer = new JsonOntologyOverlayWriter();
        var p = Proposal(RecipeProposalKind.WorkflowRecipe, concept: "fetch", body: "fetch -> summarize");

        try
        {
            var changed = await writer.MergeAsync(p, path);
            changed.Should().BeTrue();
            File.Exists(path).Should().BeTrue();
            var reloaded = OntologyOverlayLoader.LoadFromFile(path);
            reloaded.Recipes.Should().NotBeNull().And.HaveCount(1);

            // Second merge is a no-op; file unchanged.
            var firstWriteTime = File.GetLastWriteTimeUtc(path);
            await Task.Delay(20);
            var changedAgain = await writer.MergeAsync(p, path);
            changedAgain.Should().BeFalse();
            File.GetLastWriteTimeUtc(path).Should().Be(firstWriteTime);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task MergeAsync_ConcurrentWritesSerialize_NoLostUpdates()
    {
        var path = NewTempPath();
        var writer = new JsonOntologyOverlayWriter();
        var proposals = Enumerable.Range(0, 5)
            .Select(i => Proposal(RecipeProposalKind.TagSuggestion, concept: "ContainerPlugin", body: $"tag-{i}"))
            .ToArray();

        try
        {
            await Task.WhenAll(proposals.Select(p => writer.MergeAsync(p, path)));
            var reloaded = OntologyOverlayLoader.LoadFromFile(path);
            reloaded.Kinds!["ContainerPlugin"].Tags.Should().BeEquivalentTo(["tag-0", "tag-1", "tag-2", "tag-3", "tag-4"]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static RecipeProposal Proposal(RecipeProposalKind kind, string concept, string body) =>
        new()
        {
            ProposalId = Guid.NewGuid().ToString("N"),
            Kind = kind,
            Concept = concept,
            Body = body,
            Support = 3,
            Confidence = 0.5,
            SourceTraceIds = new[] { "t1" },
            RiskLevel = RecipeProposalRiskLevel.Medium,
            Status = RecipeProposalStatus.Approved,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private static string NewTempPath() =>
        Path.Combine(Path.GetTempPath(), $"vais-overlay-test-{Guid.NewGuid():N}.json");
}
