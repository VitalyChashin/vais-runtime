// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// D-14 verify gate: <see cref="HotReloadableOntologyCatalog"/> forwards reads to its inner
/// catalog and atomically swaps after <see cref="HotReloadableOntologyCatalog.ReloadAsync"/>;
/// <see cref="OverlayPublishingRecipeProposalStoreDecorator"/> publishes approved proposals
/// to the overlay file and triggers a catalog reload — so vais.describe sees the change
/// without a runtime restart.
/// </summary>
public sealed class OntologyCatalogReloaderTests
{
    [Fact]
    public async Task HotReloadable_ReadsFreshOverlayAfterReload()
    {
        var path = NewTempPath();
        try
        {
            // Start: overlay declares a description for ContainerPlugin.
            await File.WriteAllTextAsync(path,
                """{ "kinds": { "ContainerPlugin": { "description": "before" } } }""");
            var initial = OntologyCatalog.BuildFromEmbeddedBase(OntologyOverlayLoader.LoadFromFile(path));
            var sut = new HotReloadableOntologyCatalog(initial, path);

            sut.Get("ContainerPlugin").Description.Should().Be("before");

            // Mutate the file out-of-band, then reload.
            await File.WriteAllTextAsync(path,
                """{ "kinds": { "ContainerPlugin": { "description": "after" } } }""");
            await sut.ReloadAsync();

            sut.Get("ContainerPlugin").Description.Should().Be("after");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Decorator_ApproveTriggersOverlayWrite_AndCatalogReload()
    {
        var path = NewTempPath();
        try
        {
            await File.WriteAllTextAsync(path, "{}");
            var initial = OntologyCatalog.BuildFromEmbeddedBase(OntologyOverlayLoader.LoadFromFile(path));
            var catalog = new HotReloadableOntologyCatalog(initial, path);
            var inner = new InMemoryRecipeProposalStore();
            var writer = new JsonOntologyOverlayWriter();
            var sut = new OverlayPublishingRecipeProposalStoreDecorator(inner, writer, path, reloader: catalog);

            var p = Proposal(RecipeProposalKind.DescriptionRewrite, concept: "Agent", body: "Hot-reloaded description.");
            await sut.UpsertAsync(p);
            var decided = await sut.DecideAsync(p.ProposalId, approve: true, decidedBy: "alice");

            decided!.Status.Should().Be(RecipeProposalStatus.Approved);

            // File on disk reflects the new description.
            var reloaded = OntologyOverlayLoader.LoadFromFile(path);
            reloaded.Kinds!["Agent"].Description.Should().Be("Hot-reloaded description.");

            // In-process catalog reflects it without further intervention.
            catalog.Get("Agent").Description.Should().Be("Hot-reloaded description.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Decorator_RejectDoesNotPublish()
    {
        var path = NewTempPath();
        try
        {
            await File.WriteAllTextAsync(path, "{}");
            var initial = OntologyCatalog.BuildFromEmbeddedBase(OntologyOverlayLoader.LoadFromFile(path));
            var catalog = new HotReloadableOntologyCatalog(initial, path);
            var inner = new InMemoryRecipeProposalStore();
            var writer = new JsonOntologyOverlayWriter();
            var sut = new OverlayPublishingRecipeProposalStoreDecorator(inner, writer, path, reloader: catalog);

            var p = Proposal(RecipeProposalKind.DescriptionRewrite, concept: "Agent", body: "Should not land.");
            await sut.UpsertAsync(p);
            await sut.DecideAsync(p.ProposalId, approve: false, decidedBy: "alice");

            var reloaded = OntologyOverlayLoader.LoadFromFile(path);
            (reloaded.Kinds is null || !reloaded.Kinds.ContainsKey("Agent")
                || reloaded.Kinds["Agent"].Description is null)
                .Should().BeTrue();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Decorator_SideEffectFailure_DoesNotRollBackDecision_ByDefault()
    {
        // Use an invalid path with a poisoned directory component so the write fails.
        var bogusPath = Path.Combine(Path.GetTempPath(), "vais-test-bogus", "\0invalid\0", "overlay.json");
        var inner = new InMemoryRecipeProposalStore();
        var writer = new JsonOntologyOverlayWriter();
        var sut = new OverlayPublishingRecipeProposalStoreDecorator(inner, writer, bogusPath, reloader: null);

        var p = Proposal(RecipeProposalKind.DescriptionRewrite, concept: "Agent", body: "x");
        await sut.UpsertAsync(p);

        var decided = await sut.DecideAsync(p.ProposalId, approve: true, decidedBy: "alice");

        decided!.Status.Should().Be(RecipeProposalStatus.Approved, "decision is durable even when side effects fail");
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
            Status = RecipeProposalStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private static string NewTempPath() =>
        Path.Combine(Path.GetTempPath(), $"vais-overlay-reload-test-{Guid.NewGuid():N}.json");
}
