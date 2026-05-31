// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// FP-4 unit tests — <see cref="FailurePriorNameEnricher"/> builds a prompt from
/// <see cref="FailurePriorBody"/>, calls <see cref="ICompletionProvider"/>, and
/// returns the trimmed label or <c>null</c> on bad input / empty response.
/// </summary>
public sealed class FailurePriorNameEnricherTests
{
    [Fact]
    public async Task ValidBodyWithTool_ReturnsLlmName()
    {
        var body = new FailurePriorBody
        {
            AgentName = "researcher-1",
            ConceptName = "McpToolError",
            AttributionPath = "researcher-1/mcp/confluence_search",
            ToolName = "confluence_search",
            FailureCount = 12,
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-3),
            LastSeen = DateTimeOffset.UtcNow,
        };
        var proposal = Proposal(JsonSerializer.Serialize(body));
        var provider = new StubProvider("McpToolError on confluence_search (researcher-1)");

        var result = await FailurePriorNameEnricher.GenerateNameAsync(provider, proposal, default);

        result.Should().Be("McpToolError on confluence_search (researcher-1)");
    }

    [Fact]
    public async Task ValidBodyWithoutTool_ReturnsLlmName()
    {
        var body = new FailurePriorBody
        {
            AgentName = "researcher-1",
            ConceptName = "TurnFailed",
            AttributionPath = "researcher-1",
            ToolName = null,
            FailureCount = 7,
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-1),
            LastSeen = DateTimeOffset.UtcNow,
        };
        var proposal = Proposal(JsonSerializer.Serialize(body));
        var provider = new StubProvider("TurnFailed on researcher-1");

        var result = await FailurePriorNameEnricher.GenerateNameAsync(provider, proposal, default);

        result.Should().Be("TurnFailed on researcher-1");
    }

    [Fact]
    public async Task InvalidJsonBody_ReturnsNull()
    {
        var proposal = Proposal("not json at all");
        var provider = new StubProvider("should not be called");

        var result = await FailurePriorNameEnricher.GenerateNameAsync(provider, proposal, default);

        result.Should().BeNull();
        provider.CallCount.Should().Be(0, because: "bad JSON short-circuits before calling the provider");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ProviderReturnsWhitespace_ReturnsNull(string providerResponse)
    {
        var body = new FailurePriorBody
        {
            AgentName = "agent-x",
            ConceptName = "ToolError",
            AttributionPath = "agent-x/tool-y",
            ToolName = "tool-y",
            FailureCount = 5,
            FirstSeen = DateTimeOffset.UtcNow.AddHours(-1),
            LastSeen = DateTimeOffset.UtcNow,
        };
        var proposal = Proposal(JsonSerializer.Serialize(body));
        var provider = new StubProvider(providerResponse);

        var result = await FailurePriorNameEnricher.GenerateNameAsync(provider, proposal, default);

        result.Should().BeNull(because: "whitespace response is treated as 'no name produced'");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static RecipeProposal Proposal(string body) => new()
    {
        ProposalId = Guid.NewGuid().ToString("N"),
        Kind = RecipeProposalKind.FailurePrior,
        Concept = "McpToolError",
        Body = body,
        Support = 5,
        Confidence = 0.0,
        SourceTraceIds = [],
        RiskLevel = RecipeProposalRiskLevel.Low,
        Status = RecipeProposalStatus.Pending,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private sealed class StubProvider(string text) : ICompletionProvider
    {
        public int CallCount { get; private set; }
        public string ProviderName => "stub";

        public Task<CompletionResponse> CompleteAsync(
            CompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new CompletionResponse(text));
        }
    }
}
