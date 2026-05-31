// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Vais.Agents.Control;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// FP-10 unit tests — <see cref="FailurePatternInducer"/> mines a seeded
/// <see cref="IFailureSearchService"/> corpus, groups failures by
/// (ConceptName, AttributionPath), and emits deterministic proposals when
/// support meets <see cref="FailurePatternInducerOptions.MinSupport"/>.
/// </summary>
public sealed class FailurePatternInducerTests
{
    [Fact]
    public async Task InduceAsync_EmptyCorpus_ReturnsEmpty()
    {
        var inducer = new FailurePatternInducer(new StubFailureSearchService([]));
        var result = await inducer.InduceAsync(new TrajectoryQuery());
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task InduceAsync_BelowMinSupport_ReturnsEmpty()
    {
        var signals = BuildSignals("McpToolError", "agent1/mcp/search", count: 4);
        var inducer = new FailurePatternInducer(
            new StubFailureSearchService(signals),
            new FailurePatternInducerOptions { MinSupport = 5 });

        var result = await inducer.InduceAsync(new TrajectoryQuery());

        result.Should().BeEmpty("4 failures < MinSupport 5");
    }

    [Fact]
    public async Task InduceAsync_AtMinSupport_EmitsOneProposal()
    {
        var signals = BuildSignals("McpToolError", "agent1/mcp/search", count: 5);
        var inducer = new FailurePatternInducer(
            new StubFailureSearchService(signals),
            new FailurePatternInducerOptions { MinSupport = 5 });

        var result = await inducer.InduceAsync(new TrajectoryQuery());

        result.Should().HaveCount(1);
        var p = result[0];
        p.Kind.Should().Be(RecipeProposalKind.FailurePrior);
        p.Concept.Should().Be("McpToolError");
        p.Support.Should().Be(5);
        p.Confidence.Should().Be(0.0, "failure-only corpus — fail-rate denominator unavailable");
        p.RiskLevel.Should().Be(RecipeProposalRiskLevel.Low, "5 < MediumRiskMinFailureCount (10)");
        p.Status.Should().Be(RecipeProposalStatus.Pending);
    }

    [Fact]
    public async Task InduceAsync_TwoGroups_EmitsSeparateProposals()
    {
        var signals = new List<FailureSearchResult>();
        signals.AddRange(BuildSignals("McpToolError", "agent1/mcp/search", count: 5));
        signals.AddRange(BuildSignals("ToolError", "agent2/tool_name", count: 6));

        var inducer = new FailurePatternInducer(
            new StubFailureSearchService(signals),
            new FailurePatternInducerOptions { MinSupport = 5 });

        var result = await inducer.InduceAsync(new TrajectoryQuery());

        result.Should().HaveCount(2);
        result.Select(p => p.Concept).Should().BeEquivalentTo(["McpToolError", "ToolError"]);
    }

    [Fact]
    public async Task InduceAsync_BodyRoundTrips_AsFailurePriorBody()
    {
        var signals = BuildSignals("McpToolError", "agent1/mcp-server/search", count: 5);
        var inducer = new FailurePatternInducer(
            new StubFailureSearchService(signals),
            new FailurePatternInducerOptions { MinSupport = 5 });

        var result = await inducer.InduceAsync(new TrajectoryQuery());

        var body = JsonSerializer.Deserialize<FailurePriorBody>(result[0].Body);
        body.Should().NotBeNull();
        body!.AgentName.Should().Be("agent1");
        body.ConceptName.Should().Be("McpToolError");
        body.AttributionPath.Should().Be("agent1/mcp-server/search");
        body.ToolName.Should().Be("search");
        body.FailureCount.Should().Be(5);
    }

    [Fact]
    public async Task InduceAsync_HighFailureCount_ClassifiesAsMediumRisk()
    {
        var signals = BuildSignals("ToolError", "agent1/expensive_tool", count: 15);
        var inducer = new FailurePatternInducer(
            new StubFailureSearchService(signals),
            new FailurePatternInducerOptions { MinSupport = 5, MediumRiskMinFailureCount = 10 });

        var result = await inducer.InduceAsync(new TrajectoryQuery());

        result.Should().HaveCount(1);
        result[0].RiskLevel.Should().Be(RecipeProposalRiskLevel.Medium);
    }

    [Fact]
    public async Task InduceAsync_SinceFilter_PassedThroughToSearchService()
    {
        var since = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset? capturedSince = null;

        var stub = new StubFailureSearchService([], onSearch: q => capturedSince = q.Since);
        var inducer = new FailurePatternInducer(stub);

        await inducer.InduceAsync(new TrajectoryQuery(Since: since));

        capturedSince.Should().Be(since);
    }

    [Fact]
    public async Task InduceAsync_SingleSegmentPath_AgentNameIsPath_ToolNameIsNull()
    {
        // Agent-level signals (TurnFailed, LlmRetry…) stamp AttributionPath = agentId (1 segment).
        var signals = BuildSignals("TurnFailed", "agent1", count: 5);
        var inducer = new FailurePatternInducer(
            new StubFailureSearchService(signals),
            new FailurePatternInducerOptions { MinSupport = 5 });

        var result = await inducer.InduceAsync(new TrajectoryQuery());

        result.Should().HaveCount(1);
        var body = JsonSerializer.Deserialize<FailurePriorBody>(result[0].Body)!;
        body.AgentName.Should().Be("agent1", "first segment of single-segment path is the agent");
        body.ToolName.Should().BeNull("no tool component in agent-level paths");
    }

    [Fact]
    public async Task InduceAsync_Confidence_IsZero_NotOnePointZero()
    {
        // Confidence = 0.0, not 1.0 — failure-only corpus means we can't compute a real fail-rate.
        var signals = BuildSignals("ToolError", "agent1/search", count: 5);
        var inducer = new FailurePatternInducer(new StubFailureSearchService(signals),
            new FailurePatternInducerOptions { MinSupport = 5 });

        var result = await inducer.InduceAsync(new TrajectoryQuery());

        result[0].Confidence.Should().Be(0.0, "failure-only corpus — fail-rate denominator unavailable");
    }

    [Fact]
    public async Task InduceAsync_RecencySort_MostRecentGroupFirst()
    {
        var now = DateTimeOffset.UtcNow;
        var recent = BuildSignals("McpToolError", "agent1/search", count: 5, baseTime: now.AddHours(-1));
        var old = BuildSignals("ToolError", "agent2/tool", count: 6, baseTime: now.AddDays(-30));

        var inducer = new FailurePatternInducer(
            new StubFailureSearchService([.. recent, .. old]),
            new FailurePatternInducerOptions { MinSupport = 5, RecencyWeightHours = 24 });

        var result = await inducer.InduceAsync(new TrajectoryQuery());

        result.Should().HaveCount(2);
        result[0].Concept.Should().Be("McpToolError", "recent group should rank first");
    }

    private static List<FailureSearchResult> BuildSignals(
        string concept, string path, int count, DateTimeOffset? baseTime = null)
    {
        var t = baseTime ?? DateTimeOffset.UtcNow.AddHours(-1);
        return Enumerable.Range(0, count)
            .Select(i => new FailureSearchResult(
                RunId: $"run-{i}",
                ConceptName: concept,
                AttributionPath: path,
                Source: path.Split('/')[0],
                Level: "warning",
                ErrorType: "SomeException",
                At: t.AddMinutes(i)))
            .ToList();
    }

    private sealed class StubFailureSearchService(
        IReadOnlyList<FailureSearchResult> results,
        Action<FailureSearchQuery>? onSearch = null) : IFailureSearchService
    {
        public Task<IReadOnlyList<FailureSearchResult>> SearchAsync(
            FailureSearchQuery query, CancellationToken ct = default)
        {
            onSearch?.Invoke(query);
            return Task.FromResult(results);
        }
    }
}
