// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vais.Agents.Core;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// D-11 + D-12: recipes endpoints round-trip through the typed client; the propose path
/// runs an injected <see cref="IRecipeInducer"/> and persists the output; decide flips
/// status; high-risk approval is delegated to the gate (returns 202 via ProblemDetails
/// when the gate throws <see cref="ApprovalRequiredException"/>).
/// </summary>
public sealed class RecipesEndpointTests : IAsyncLifetime
{
    private IHost _host = null!;
    private IAgentControlPlaneClient _client = null!;
    private InMemoryRecipeProposalStore _store = null!;
    private StubInducer _inducer = null!;

    public async Task InitializeAsync()
    {
        _inducer = new StubInducer();
        _store = new InMemoryRecipeProposalStore(highRiskApprovalCheck: (p, _, _) =>
        {
            // Stand-in gate: any High-risk approval is held the first time, allowed the second.
            if (p.RiskLevel == RecipeProposalRiskLevel.High && !_approved.Contains(p.ProposalId))
            {
                _approved.Add(p.ProposalId); // next call will pass
                throw new ApprovalRequiredException("Recipe", p.ProposalId, $"req-{p.ProposalId}");
            }
            return ValueTask.CompletedTask;
        });
        _host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddSingleton<IRecipeProposalStore>(_store);
                    services.AddSingleton<IRecipeInducer>(_inducer);
                    services.AddRouting();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapRecipeControlPlane());
                });
            })
            .StartAsync();

        var http = _host.GetTestClient();
        http.BaseAddress ??= new Uri("http://localhost");
        _client = new AgentControlPlaneClient(http);
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task EmptyStore_ListIsEmpty_GetIsNull()
    {
        (await _client.ListRecipesAsync()).Should().BeEmpty();
        (await _client.GetRecipeAsync("ghost")).Should().BeNull();
    }

    [Fact]
    public async Task Propose_RunsInducer_PersistsProposals()
    {
        _inducer.Next = [Proposal("p1", risk: RecipeProposalRiskLevel.Low),
                         Proposal("p2", risk: RecipeProposalRiskLevel.High)];

        var emitted = await _client.ProposeRecipesAsync();

        emitted.Should().HaveCount(2);
        var listed = await _client.ListRecipesAsync();
        listed.Select(p => p.ProposalId).Should().BeEquivalentTo(["p1", "p2"]);
    }

    [Fact]
    public async Task Propose_ForwardsTrajectoryFilters()
    {
        _inducer.Next = [Proposal("p1")];
        await _client.ProposeRecipesAsync(agent: "coord", concept: "fetch");

        _inducer.LastQuery.Should().NotBeNull();
        _inducer.LastQuery!.AgentId.Should().Be("coord");
        _inducer.LastQuery.ConceptName.Should().Be("fetch");
    }

    [Fact]
    public async Task DecideLowRisk_FlipsApprovedDirectly()
    {
        await _store.UpsertAsync(Proposal("p1", risk: RecipeProposalRiskLevel.Low));

        var decided = await _client.DecideRecipeAsync("p1", approve: true, decidedBy: "alice");

        decided!.Status.Should().Be(RecipeProposalStatus.Approved);
        decided.ReviewerId.Should().Be("alice");
    }

    [Fact]
    public async Task DecideHighRisk_FirstAttemptThrowsApprovalRequired_SecondAttemptSucceeds()
    {
        await _store.UpsertAsync(Proposal("p-high", risk: RecipeProposalRiskLevel.High));

        // First attempt: the stub gate throws → 202 ProblemDetails → client raises the typed exception.
        var thrown = await FluentActions.Awaiting(() => _client.DecideRecipeAsync("p-high", approve: true, decidedBy: "alice"))
            .Should().ThrowAsync<ApprovalRequiredException>();
        thrown.Which.Kind.Should().Be("Recipe");
        thrown.Which.Name.Should().Be("p-high");
        thrown.Which.RequestId.Should().Be("req-p-high");

        var stillPending = await _client.GetRecipeAsync("p-high");
        stillPending!.Status.Should().Be(RecipeProposalStatus.Pending);

        // Second attempt: the gate now passes (operator "approved" the underlying request).
        var decided = await _client.DecideRecipeAsync("p-high", approve: true, decidedBy: "alice");
        decided!.Status.Should().Be(RecipeProposalStatus.Approved);
    }

    [Fact]
    public async Task DecideHighRiskReject_BypassesGate()
    {
        await _store.UpsertAsync(Proposal("p-high", risk: RecipeProposalRiskLevel.High));

        var rejected = await _client.DecideRecipeAsync("p-high", approve: false, decidedBy: "alice");

        rejected!.Status.Should().Be(RecipeProposalStatus.Rejected);
    }

    [Fact]
    public async Task DecideUnknown_Returns404()
    {
        (await _client.DecideRecipeAsync("ghost", approve: true, decidedBy: "alice")).Should().BeNull();
    }

    [Fact]
    public async Task List_FiltersByStatus_OnServer()
    {
        await _store.UpsertAsync(Proposal("p1"));
        await _store.UpsertAsync(Proposal("p2"));
        await _store.DecideAsync("p2", approve: true, decidedBy: "alice");

        var pendingOnly = await _client.ListRecipesAsync(status: "Pending");
        pendingOnly.Select(p => p.ProposalId).Should().Equal("p1");
    }

    [Fact]
    public async Task Returns503_WhenStoreNotRegistered()
    {
        using var bareHost = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services => services.AddRouting());
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapRecipeControlPlane());
                });
            }).StartAsync();
        var bareHttp = bareHost.GetTestClient();
        bareHttp.BaseAddress ??= new Uri("http://localhost");
        var bareClient = new AgentControlPlaneClient(bareHttp);

        await FluentActions.Awaiting(() => bareClient.ListRecipesAsync())
            .Should().ThrowAsync<AgentControlPlaneException>();
        await bareHost.StopAsync();
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private readonly HashSet<string> _approved = new(StringComparer.Ordinal);

    private static RecipeProposal Proposal(string id, RecipeProposalRiskLevel risk = RecipeProposalRiskLevel.Medium) =>
        new()
        {
            ProposalId = id,
            Kind = RecipeProposalKind.WorkflowRecipe,
            Concept = "fetch",
            Body = "fetch -> summarize",
            Support = 3,
            Confidence = 0.5,
            SourceTraceIds = new[] { "t1" },
            RiskLevel = risk,
            Status = RecipeProposalStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private sealed class StubInducer : IRecipeInducer
    {
        public IReadOnlyList<RecipeProposal> Next { get; set; } = Array.Empty<RecipeProposal>();
        public TrajectoryQuery? LastQuery { get; private set; }

        public Task<IReadOnlyList<RecipeProposal>> InduceAsync(TrajectoryQuery query, CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return Task.FromResult(Next);
        }
    }
}
