// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests.Telemetry;

/// <summary>
/// D-8 verify gate: behavioral inducer mines ordered n-gram sequence patterns from the
/// trajectory corpus. Support is "distinct runs that exhibit the pattern", confidence is
/// support/totalRuns. High-risk classification fires on destructive concept substrings.
/// </summary>
public sealed class BehavioralRecipeInducerTests
{
    [Fact]
    public async Task EmptyCorpus_YieldsNoProposals()
    {
        var store = new InMemoryInterceptorTeeStore();
        var inducer = new BehavioralRecipeInducer(store);

        var proposals = await inducer.InduceAsync(new TrajectoryQuery());

        proposals.Should().BeEmpty();
    }

    [Fact]
    public async Task PatternBelowMinSupport_NotProposed()
    {
        var store = new InMemoryInterceptorTeeStore();
        await Run(store, "r1", "fetch", "summarize");
        await Run(store, "r2", "fetch", "summarize");
        var inducer = new BehavioralRecipeInducer(store, new() { MinSupport = 3 });

        var proposals = await inducer.InduceAsync(new TrajectoryQuery());

        proposals.Should().BeEmpty();
    }

    [Fact]
    public async Task RepeatedPair_Proposed_WithSupportAndConfidence()
    {
        var store = new InMemoryInterceptorTeeStore();
        await Run(store, "r1", "fetch", "summarize");
        await Run(store, "r2", "fetch", "summarize");
        await Run(store, "r3", "fetch", "summarize");
        await Run(store, "r4", "unrelated");
        var inducer = new BehavioralRecipeInducer(store, new() { MinSupport = 3, MaxSequenceLength = 2 });

        var proposals = await inducer.InduceAsync(new TrajectoryQuery());

        proposals.Should().ContainSingle();
        var p = proposals[0];
        p.Kind.Should().Be(RecipeProposalKind.WorkflowRecipe);
        p.Support.Should().Be(3);
        p.Confidence.Should().BeApproximately(0.75, 1e-9);
        p.Body.Should().Be("fetch -> summarize");
        p.Concept.Should().Be("summarize"); // anchor = last concept
        p.Status.Should().Be(RecipeProposalStatus.Pending);
        p.RiskLevel.Should().Be(RecipeProposalRiskLevel.Medium); // length-2, no destructive marker
        p.SourceTraceIds.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SameRunDoesNotDoubleCountPattern()
    {
        var store = new InMemoryInterceptorTeeStore();
        // Same pattern appears three times in a single run → support is still 1.
        await Run(store, "r1", "fetch", "summarize", "fetch", "summarize", "fetch", "summarize");
        var inducer = new BehavioralRecipeInducer(store, new() { MinSupport = 2, MaxSequenceLength = 2 });

        var proposals = await inducer.InduceAsync(new TrajectoryQuery());

        proposals.Should().BeEmpty("support counts distinct runs, not occurrences within a run");
    }

    [Fact]
    public async Task DestructiveConcept_FlagsHighRisk_EvenForLongSequence()
    {
        var store = new InMemoryInterceptorTeeStore();
        await Run(store, "r1", "build", "test", "deploy");
        await Run(store, "r2", "build", "test", "deploy");
        var inducer = new BehavioralRecipeInducer(store, new() { MinSupport = 2, MaxSequenceLength = 3 });

        var proposals = await inducer.InduceAsync(new TrajectoryQuery());

        var triple = proposals.Should().Contain(p => p.Body == "build -> test -> deploy").Which;
        triple.RiskLevel.Should().Be(RecipeProposalRiskLevel.High);
    }

    [Fact]
    public async Task LongPair_LowRiskUntilDestructiveAppears()
    {
        var store = new InMemoryInterceptorTeeStore();
        await Run(store, "r1", "read", "transform", "write");
        await Run(store, "r2", "read", "transform", "write");
        await Run(store, "r3", "read", "transform", "write");
        var inducer = new BehavioralRecipeInducer(store, new() { MinSupport = 3, MaxSequenceLength = 3 });

        var proposals = await inducer.InduceAsync(new TrajectoryQuery());

        var triple = proposals.Should().Contain(p => p.Body == "read -> transform -> write").Which;
        triple.RiskLevel.Should().Be(RecipeProposalRiskLevel.Low);
    }

    [Fact]
    public async Task SortedBySupportDescThenConfidenceDesc()
    {
        var store = new InMemoryInterceptorTeeStore();
        // High-support pair "a → b"
        for (var i = 0; i < 4; i++) await Run(store, $"hi{i}", "a", "b");
        // Lower-support pair "c → d"
        await Run(store, "lo1", "c", "d");
        await Run(store, "lo2", "c", "d");
        var inducer = new BehavioralRecipeInducer(store, new() { MinSupport = 2, MaxSequenceLength = 2 });

        var proposals = await inducer.InduceAsync(new TrajectoryQuery());

        proposals.Should().HaveCount(2);
        proposals[0].Body.Should().Be("a -> b");
        proposals[1].Body.Should().Be("c -> d");
    }

    [Fact]
    public async Task EventsWithoutConceptName_AreSkipped()
    {
        var store = new InMemoryInterceptorTeeStore();
        await store.AppendAsync(Event("e1", run: "r1", concept: null));
        await store.AppendAsync(Event("e2", run: "r1", concept: "fetch"));
        await store.AppendAsync(Event("e3", run: "r1", concept: "summarize"));
        await store.AppendAsync(Event("e4", run: "r2", concept: "fetch"));
        await store.AppendAsync(Event("e5", run: "r2", concept: "summarize"));
        var inducer = new BehavioralRecipeInducer(store, new() { MinSupport = 2, MaxSequenceLength = 2 });

        var proposals = await inducer.InduceAsync(new TrajectoryQuery());

        proposals.Should().ContainSingle()
            .Which.Body.Should().Be("fetch -> summarize");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static async Task Run(InMemoryInterceptorTeeStore store, string runId, params string[] concepts)
    {
        var t = DateTimeOffset.UtcNow.AddSeconds(-concepts.Length);
        for (var i = 0; i < concepts.Length; i++)
        {
            await store.AppendAsync(Event($"{runId}:{i}", run: runId, concept: concepts[i], at: t.AddSeconds(i)));
        }
    }

    private static TrajectoryEvent Event(string id, string? run = null, string? concept = null, DateTimeOffset? at = null) =>
        new()
        {
            EventId = id,
            Timestamp = at ?? DateTimeOffset.UtcNow,
            EventName = "tool.call",
            Operation = OntologyOperation.Call,
            RunId = run,
            ConceptName = concept,
            Transport = "south",
        };
}
