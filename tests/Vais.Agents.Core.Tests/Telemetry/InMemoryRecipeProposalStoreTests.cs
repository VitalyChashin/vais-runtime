// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests.Telemetry;

/// <summary>
/// D-10 verify gate: in-memory proposal store. Upsert preserves human-decision fields;
/// list filters + newest-first sort; status transitions are sticky; high-risk gate is
/// invoked only when approving High-risk proposals.
/// </summary>
public sealed class InMemoryRecipeProposalStoreTests
{
    [Fact]
    public async Task EmptyStore_GetReturnsNull_ListEmpty()
    {
        var store = new InMemoryRecipeProposalStore();

        (await store.GetAsync("nope")).Should().BeNull();
        var listed = await Collect(store.ListAsync(new RecipeProposalQuery()));
        listed.Should().BeEmpty();
    }

    [Fact]
    public async Task Upsert_ThenGet_ReturnsProposal()
    {
        var store = new InMemoryRecipeProposalStore();
        var p = Proposal("p1");
        await store.UpsertAsync(p);

        var got = await store.GetAsync("p1");
        got.Should().Be(p);
    }

    [Fact]
    public async Task ReUpsert_PreservesDecidedStatusAndReviewer()
    {
        var store = new InMemoryRecipeProposalStore();
        var p = Proposal("p1");
        await store.UpsertAsync(p);
        await store.DecideAsync("p1", approve: true, decidedBy: "alice");

        // Re-induction arrives as Pending — must NOT clobber the decision.
        await store.UpsertAsync(p with { Status = RecipeProposalStatus.Pending, Support = 42 });
        var got = (await store.GetAsync("p1"))!;

        got.Status.Should().Be(RecipeProposalStatus.Approved);
        got.ReviewerId.Should().Be("alice");
        got.ReviewedAt.Should().NotBeNull();
        got.Support.Should().Be(42, "non-decision fields still update");
    }

    [Fact]
    public async Task ListAsync_FiltersByConceptKindStatusRiskLevel_NewestFirst()
    {
        var store = new InMemoryRecipeProposalStore();
        var now = DateTimeOffset.UtcNow;
        await store.UpsertAsync(Proposal("p1", concept: "fetch", risk: RecipeProposalRiskLevel.Low, createdAt: now.AddSeconds(-3)));
        await store.UpsertAsync(Proposal("p2", concept: "fetch", risk: RecipeProposalRiskLevel.High, createdAt: now.AddSeconds(-2)));
        await store.UpsertAsync(Proposal("p3", concept: "summarize", risk: RecipeProposalRiskLevel.Medium, createdAt: now.AddSeconds(-1)));

        var fetchOnly = await Collect(store.ListAsync(new RecipeProposalQuery(Concept: "fetch")));
        fetchOnly.Select(p => p.ProposalId).Should().Equal("p2", "p1");

        var highOnly = await Collect(store.ListAsync(new RecipeProposalQuery(RiskLevel: RecipeProposalRiskLevel.High)));
        highOnly.Should().ContainSingle().Which.ProposalId.Should().Be("p2");
    }

    [Fact]
    public async Task DecideAsync_LowRisk_BypassesGate_FlipsApproved()
    {
        var gateCalls = 0;
        var store = new InMemoryRecipeProposalStore((_, _, _) => { gateCalls++; return ValueTask.CompletedTask; });
        await store.UpsertAsync(Proposal("p1", risk: RecipeProposalRiskLevel.Low));

        var result = await store.DecideAsync("p1", approve: true, decidedBy: "alice");

        result!.Status.Should().Be(RecipeProposalStatus.Approved);
        result.ReviewerId.Should().Be("alice");
        gateCalls.Should().Be(0, "gate fires only for High-risk");
    }

    [Fact]
    public async Task DecideAsync_HighRiskApprove_InvokesGate()
    {
        var gateCalls = 0;
        var store = new InMemoryRecipeProposalStore((_, _, _) => { gateCalls++; return ValueTask.CompletedTask; });
        await store.UpsertAsync(Proposal("p1", risk: RecipeProposalRiskLevel.High));

        await store.DecideAsync("p1", approve: true, decidedBy: "alice");

        gateCalls.Should().Be(1);
    }

    [Fact]
    public async Task DecideAsync_HighRiskGateThrows_ProposalStaysPending()
    {
        var store = new InMemoryRecipeProposalStore((_, _, _) => throw new InvalidOperationException("approval required"));
        await store.UpsertAsync(Proposal("p1", risk: RecipeProposalRiskLevel.High));

        await FluentActions.Awaiting(() => store.DecideAsync("p1", approve: true, decidedBy: "alice").AsTask())
            .Should().ThrowAsync<InvalidOperationException>();

        var still = await store.GetAsync("p1");
        still!.Status.Should().Be(RecipeProposalStatus.Pending);
        still.ReviewerId.Should().BeNull();
    }

    [Fact]
    public async Task DecideAsync_HighRiskReject_BypassesGate()
    {
        var store = new InMemoryRecipeProposalStore((_, _, _) => throw new InvalidOperationException("should not fire"));
        await store.UpsertAsync(Proposal("p1", risk: RecipeProposalRiskLevel.High));

        var result = await store.DecideAsync("p1", approve: false, decidedBy: "alice");

        result!.Status.Should().Be(RecipeProposalStatus.Rejected);
    }

    [Fact]
    public async Task DecideAsync_AlreadyDecided_Sticky()
    {
        var store = new InMemoryRecipeProposalStore();
        await store.UpsertAsync(Proposal("p1"));
        await store.DecideAsync("p1", approve: true, decidedBy: "alice");

        var second = await store.DecideAsync("p1", approve: false, decidedBy: "bob");

        second!.Status.Should().Be(RecipeProposalStatus.Approved);
        second.ReviewerId.Should().Be("alice");
    }

    [Fact]
    public async Task DecideAsync_UnknownId_ReturnsNull()
    {
        var store = new InMemoryRecipeProposalStore();

        (await store.DecideAsync("ghost", approve: true, decidedBy: "alice")).Should().BeNull();
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static RecipeProposal Proposal(
        string id,
        string concept = "fetch",
        RecipeProposalRiskLevel risk = RecipeProposalRiskLevel.Medium,
        DateTimeOffset? createdAt = null) =>
        new()
        {
            ProposalId = id,
            Kind = RecipeProposalKind.WorkflowRecipe,
            Concept = concept,
            Body = "fetch -> summarize",
            Support = 3,
            Confidence = 0.5,
            SourceTraceIds = new[] { "t1" },
            RiskLevel = risk,
            Status = RecipeProposalStatus.Pending,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        };

    private static async Task<List<RecipeProposal>> Collect(IAsyncEnumerable<RecipeProposal> stream)
    {
        var list = new List<RecipeProposal>();
        await foreach (var p in stream) list.Add(p);
        return list;
    }
}
