// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// FP-10 unit tests — <see cref="JsonFailureOntologyOverlayWriter"/> merges an approved
/// <see cref="RecipeProposalKind.FailurePrior"/> proposal into the
/// <see cref="FailureOntologyOverlay"/> JSON.
/// </summary>
public sealed class FailureOntologyOverlayWriterTests
{
    [Fact]
    public void Merge_FailurePrior_WritesToAttributionsMap()
    {
        var existing = FailureOntologyOverlay.Empty;
        var p = FailurePriorProposal("McpToolError", "agent1/mcp/search", 5);

        var merged = JsonFailureOntologyOverlayWriter.Merge(existing, p);

        merged.Attributions.Should().HaveCount(1);
        var entry = merged.Attributions!["agent1/mcp/search"];
        entry.FailurePriors.Should().HaveCount(1);
        entry.FailurePriors![0].ConceptName.Should().Be("McpToolError");
        entry.FailurePriors[0].FailureCount.Should().Be(5);
    }

    [Fact]
    public void Merge_SameConcept_SamePath_ReplacesExisting_Idempotent()
    {
        var existing = FailureOntologyOverlay.Empty;
        var p = FailurePriorProposal("McpToolError", "agent1/mcp/search", 5);

        var first = JsonFailureOntologyOverlayWriter.Merge(existing, p);
        var second = JsonFailureOntologyOverlayWriter.Merge(first, p);

        second.Should().BeSameAs(first, "idempotent merge returns same reference");
        second.Attributions!["agent1/mcp/search"].FailurePriors.Should().HaveCount(1);
    }

    [Fact]
    public void Merge_DifferentConcept_SamePath_AppendsBoth()
    {
        var existing = FailureOntologyOverlay.Empty;
        var p1 = FailurePriorProposal("McpToolError", "agent1/mcp/search", 5);
        var p2 = FailurePriorProposal("ToolError", "agent1/mcp/search", 3);

        var merged = JsonFailureOntologyOverlayWriter.Merge(
            JsonFailureOntologyOverlayWriter.Merge(existing, p1), p2);

        merged.Attributions!["agent1/mcp/search"].FailurePriors.Should().HaveCount(2);
    }

    [Fact]
    public void Merge_DifferentPath_CreatesSeparateEntry()
    {
        var existing = FailureOntologyOverlay.Empty;
        var p1 = FailurePriorProposal("McpToolError", "agent1/search", 5);
        var p2 = FailurePriorProposal("McpToolError", "agent2/search", 5);

        var merged = JsonFailureOntologyOverlayWriter.Merge(
            JsonFailureOntologyOverlayWriter.Merge(existing, p1), p2);

        merged.Attributions.Should().HaveCount(2);
    }

    [Fact]
    public void Merge_NonFailurePriorKind_ReturnsSameReference()
    {
        var existing = FailureOntologyOverlay.Empty;
        var p = new RecipeProposal
        {
            ProposalId = "x",
            Kind = RecipeProposalKind.TagSuggestion,
            Concept = "foo",
            Body = "tag",
            Support = 1,
            Confidence = 1.0,
            SourceTraceIds = [],
            RiskLevel = RecipeProposalRiskLevel.Low,
            Status = RecipeProposalStatus.Approved,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var result = JsonFailureOntologyOverlayWriter.Merge(existing, p);
        result.Should().BeSameAs(existing);
    }

    [Fact]
    public async Task MergeAsync_WritesToFileAndRoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vais-fp-test-{Guid.NewGuid():N}.failure-ontology.json");
        var writer = new JsonFailureOntologyOverlayWriter();
        var p = FailurePriorProposal("McpToolError", "agent1/mcp/search", 5);

        try
        {
            var changed = await writer.MergeAsync(p, path);
            changed.Should().BeTrue();
            File.Exists(path).Should().BeTrue();

            var reloaded = FailureOntologyOverlayLoader.LoadFromFile(path);
            reloaded.Attributions.Should().NotBeNull();
            reloaded.Attributions!["agent1/mcp/search"].FailurePriors.Should().HaveCount(1);

            // Second merge is idempotent — file NOT updated.
            var changedAgain = await writer.MergeAsync(p, path);
            changedAgain.Should().BeFalse();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static RecipeProposal FailurePriorProposal(string concept, string path, int failureCount)
    {
        var body = new FailurePriorBody
        {
            AgentName = path.Split('/')[0],
            ConceptName = concept,
            AttributionPath = path,
            ToolName = path.Split('/').Last(),
            FailureCount = failureCount,
            FirstSeen = DateTimeOffset.UtcNow.AddHours(-1),
            LastSeen = DateTimeOffset.UtcNow,
        };
        return new RecipeProposal
        {
            ProposalId = Guid.CreateVersion7().ToString("N"),
            Kind = RecipeProposalKind.FailurePrior,
            Concept = concept,
            Body = JsonSerializer.Serialize(body),
            Support = failureCount,
            Confidence = 1.0,
            SourceTraceIds = [],
            RiskLevel = RecipeProposalRiskLevel.Low,
            Status = RecipeProposalStatus.Approved,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }
}
