// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests.Telemetry;

/// <summary>
/// D-9 verify gate: LLM-assisted decorator enriches the <see cref="RecipeProposal.Name"/>
/// only. Body/support/confidence/risk/status are untouched. Enricher failures degrade
/// gracefully — the original proposal passes through.
/// </summary>
public sealed class LlmAssistedRecipeInducerTests
{
    [Fact]
    public async Task EmptyInner_ShortCircuits_EnricherNeverCalled()
    {
        var calls = 0;
        var inner = new StubInducer([]);
        var sut = new LlmAssistedRecipeInducer(inner, (_, _) => { calls++; return Task.FromResult<string?>("x"); });

        var result = await sut.InduceAsync(new TrajectoryQuery());

        result.Should().BeEmpty();
        calls.Should().Be(0);
    }

    [Fact]
    public async Task Enricher_PopulatesName_LeavesOtherFieldsUntouched()
    {
        var p = Proposal("p1", body: "a -> b");
        var inner = new StubInducer([p]);
        var sut = new LlmAssistedRecipeInducer(inner, (_, _) => Task.FromResult<string?>("Fetch then summarize"));

        var result = await sut.InduceAsync(new TrajectoryQuery());

        result.Should().ContainSingle();
        result[0].Name.Should().Be("Fetch then summarize");
        result[0].Body.Should().Be(p.Body);
        result[0].Support.Should().Be(p.Support);
        result[0].Confidence.Should().Be(p.Confidence);
        result[0].RiskLevel.Should().Be(p.RiskLevel);
        result[0].Status.Should().Be(p.Status);
        result[0].ProposalId.Should().Be(p.ProposalId);
    }

    [Fact]
    public async Task EnricherThrows_OriginalProposalPreserved()
    {
        var p = Proposal("p1");
        var inner = new StubInducer([p]);
        var sut = new LlmAssistedRecipeInducer(inner, (_, _) => throw new InvalidOperationException("LLM unhealthy"));

        var result = await sut.InduceAsync(new TrajectoryQuery());

        result.Should().ContainSingle();
        result[0].Name.Should().BeNull();
        result[0].Should().BeEquivalentTo(p);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NullOrWhitespaceEnrichment_KeepsOriginal(string? name)
    {
        var p = Proposal("p1");
        var inner = new StubInducer([p]);
        var sut = new LlmAssistedRecipeInducer(inner, (_, _) => Task.FromResult(name));

        var result = await sut.InduceAsync(new TrajectoryQuery());

        result[0].Name.Should().BeNull();
    }

    [Fact]
    public async Task Cancellation_PropagatesFromEnricher()
    {
        using var cts = new CancellationTokenSource();
        var inner = new StubInducer([Proposal("p1")]);
        var sut = new LlmAssistedRecipeInducer(inner, (_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<string?>("never");
        });

        await FluentActions.Awaiting(() => sut.InduceAsync(new TrajectoryQuery(), cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task EnrichmentTrimmed()
    {
        var inner = new StubInducer([Proposal("p1")]);
        var sut = new LlmAssistedRecipeInducer(inner, (_, _) => Task.FromResult<string?>("  Padded Name  "));

        var result = await sut.InduceAsync(new TrajectoryQuery());

        result[0].Name.Should().Be("Padded Name");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static RecipeProposal Proposal(string id, string body = "x -> y") =>
        new()
        {
            ProposalId = id,
            Kind = RecipeProposalKind.WorkflowRecipe,
            Concept = "y",
            Body = body,
            Support = 5,
            Confidence = 0.5,
            SourceTraceIds = new[] { "t1", "t2" },
            RiskLevel = RecipeProposalRiskLevel.Medium,
            Status = RecipeProposalStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private sealed class StubInducer(IReadOnlyList<RecipeProposal> output) : IRecipeInducer
    {
        public Task<IReadOnlyList<RecipeProposal>> InduceAsync(TrajectoryQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult(output);
    }
}
