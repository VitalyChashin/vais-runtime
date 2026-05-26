// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit;

namespace Vais.Agents.Observability.RecipeProposalStore.Tests;

/// <summary>
/// D-10 integration tests for <see cref="PostgresRecipeProposalStore"/>. Spins up an
/// ephemeral <c>postgres:16-alpine</c>, applies the schema, exercises upsert + get + list +
/// decide + retention + high-risk gate. Requires Docker.
/// </summary>
public sealed class PostgresRecipeProposalStoreTests : IAsyncLifetime
{
    private PostgreSqlContainer _postgres = null!;
    private PostgresRecipeProposalStore _store = null!;

    public async Task InitializeAsync()
    {
        _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await _postgres.StartAsync();
        _store = new PostgresRecipeProposalStore(_postgres.GetConnectionString(), NullLogger<PostgresRecipeProposalStore>.Instance);
        await _store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Schema_IsIdempotent()
    {
        await _store.InitializeAsync();
        await _store.InitializeAsync();
    }

    [Fact]
    public async Task Upsert_ThenGet_RoundTripsAllFields()
    {
        var p = Proposal("p1", concept: "fetch", body: "fetch -> summarize", support: 7, conf: 0.42, risk: RecipeProposalRiskLevel.Medium,
            traceIds: ["t1", "t2"]);

        await _store.UpsertAsync(p);
        var got = await _store.GetAsync("p1");

        got.Should().NotBeNull();
        got!.Concept.Should().Be("fetch");
        got.Body.Should().Be("fetch -> summarize");
        got.Support.Should().Be(7);
        got.Confidence.Should().BeApproximately(0.42, 1e-9);
        got.RiskLevel.Should().Be(RecipeProposalRiskLevel.Medium);
        got.SourceTraceIds.Should().Equal("t1", "t2");
        got.Status.Should().Be(RecipeProposalStatus.Pending);
    }

    [Fact]
    public async Task ReUpsert_PreservesDecidedStatus_AndReviewerFields()
    {
        var p = Proposal("p1");
        await _store.UpsertAsync(p);
        await _store.DecideAsync("p1", approve: true, decidedBy: "alice");

        // Re-induced proposal arrives as Pending — must NOT overwrite the Approved decision.
        await _store.UpsertAsync(p with { Status = RecipeProposalStatus.Pending, Support = 99 });
        var got = (await _store.GetAsync("p1"))!;

        got.Status.Should().Be(RecipeProposalStatus.Approved);
        got.ReviewerId.Should().Be("alice");
        got.ReviewedAt.Should().NotBeNull();
        got.Support.Should().Be(99, "non-decision fields still refresh");
    }

    [Fact]
    public async Task DecideAsync_FlipsPendingToApproved_LowRiskBypassesGate()
    {
        var p = Proposal("p1", risk: RecipeProposalRiskLevel.Low);
        await _store.UpsertAsync(p);

        var updated = await _store.DecideAsync("p1", approve: true, decidedBy: "alice");

        updated!.Status.Should().Be(RecipeProposalStatus.Approved);
        updated.ReviewerId.Should().Be("alice");
    }

    [Fact]
    public async Task DecideAsync_AlreadyDecided_Sticky()
    {
        var p = Proposal("p1");
        await _store.UpsertAsync(p);
        await _store.DecideAsync("p1", approve: true, decidedBy: "alice");

        var second = await _store.DecideAsync("p1", approve: false, decidedBy: "bob");

        second!.Status.Should().Be(RecipeProposalStatus.Approved);
        second.ReviewerId.Should().Be("alice");
    }

    [Fact]
    public async Task HighRiskApprove_InvokesGate_ThrowsWhenGateThrows()
    {
        var gateCalls = 0;
        var store = new PostgresRecipeProposalStore(
            _postgres.GetConnectionString(),
            NullLogger<PostgresRecipeProposalStore>.Instance,
            highRiskApprovalCheck: (_, _, _) => { gateCalls++; throw new InvalidOperationException("ApprovalRequired-stub"); });
        await store.InitializeAsync();
        var p = Proposal("p-high", risk: RecipeProposalRiskLevel.High);
        await store.UpsertAsync(p);

        await FluentActions.Awaiting(() => store.DecideAsync("p-high", approve: true, decidedBy: "alice").AsTask())
            .Should().ThrowAsync<InvalidOperationException>();

        gateCalls.Should().Be(1);
        var stillPending = await store.GetAsync("p-high");
        stillPending!.Status.Should().Be(RecipeProposalStatus.Pending);
    }

    [Fact]
    public async Task HighRiskReject_BypassesGate()
    {
        var store = new PostgresRecipeProposalStore(
            _postgres.GetConnectionString(),
            NullLogger<PostgresRecipeProposalStore>.Instance,
            highRiskApprovalCheck: (_, _, _) => throw new InvalidOperationException("gate should not fire"));
        await store.InitializeAsync();
        var p = Proposal("p-high-rej", risk: RecipeProposalRiskLevel.High);
        await store.UpsertAsync(p);

        var rejected = await store.DecideAsync("p-high-rej", approve: false, decidedBy: "alice");

        rejected!.Status.Should().Be(RecipeProposalStatus.Rejected);
    }

    [Fact]
    public async Task ListAsync_FiltersAndSortsNewestFirst()
    {
        for (var i = 0; i < 5; i++)
        {
            await _store.UpsertAsync(Proposal($"p{i}", concept: i % 2 == 0 ? "fetch" : "summarize",
                createdAt: DateTimeOffset.UtcNow.AddSeconds(i)));
        }

        var fetchOnly = await Collect(_store.ListAsync(new RecipeProposalQuery(Concept: "fetch")));
        fetchOnly.Should().HaveCount(3);
        fetchOnly.Should().BeInDescendingOrder(p => p.CreatedAt);
    }

    [Fact]
    public async Task RetentionPrune_DeletesOldDecided_KeepsPending()
    {
        await _store.UpsertAsync(Proposal("old-pending", createdAt: DateTimeOffset.UtcNow.AddDays(-100)));
        await _store.UpsertAsync(Proposal("old-decided", createdAt: DateTimeOffset.UtcNow.AddDays(-100)));
        await _store.DecideAsync("old-decided", approve: true, decidedBy: "alice");
        // Backdate the decided proposal's reviewed_at via a synthetic upsert (we just decided it now).
        // Use SQL escape: just call prune with a cutoff in the future.
        await _store.DeleteDecidedProposalsOlderThanAsync(DateTimeOffset.UtcNow.AddYears(1));

        var pending = await _store.GetAsync("old-pending");
        var decided = await _store.GetAsync("old-decided");

        pending.Should().NotBeNull("pending proposals are never pruned");
        decided.Should().BeNull("decided proposals past the cutoff are pruned");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static RecipeProposal Proposal(
        string id,
        string concept = "fetch",
        string body = "fetch -> summarize",
        int support = 3,
        double conf = 0.5,
        RecipeProposalRiskLevel risk = RecipeProposalRiskLevel.Medium,
        IReadOnlyList<string>? traceIds = null,
        DateTimeOffset? createdAt = null) =>
        new()
        {
            ProposalId = id,
            Kind = RecipeProposalKind.WorkflowRecipe,
            Concept = concept,
            Body = body,
            Support = support,
            Confidence = conf,
            SourceTraceIds = traceIds ?? new[] { "t1" },
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
