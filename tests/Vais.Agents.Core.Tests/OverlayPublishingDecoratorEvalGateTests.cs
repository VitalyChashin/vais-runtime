// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// FP-8 — <see cref="OverlayPublishingRecipeProposalStoreDecorator"/> eval-gate wiring tests.
/// Verifies that the gate is called before the overlay write for FailurePrior approvals and
/// that non-FailurePrior kinds skip the gate entirely.
/// </summary>
public sealed class OverlayPublishingDecoratorEvalGateTests
{
    // ── fakes ────────────────────────────────────────────────────────────────

    private sealed class FakeGate : IFailurePriorEvalGate
    {
        public int CallCount { get; private set; }
        public bool ReturnPassed { get; set; } = true;
        public string? ReturnReason { get; set; }

        public Task<(bool Passed, string? Reason)> EvaluateAsync(RecipeProposal prior, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult((ReturnPassed, ReturnReason));
        }
    }

    private sealed class FakeFailureWriter : IFailureOntologyOverlayWriter
    {
        public int CallCount { get; private set; }

        public Task<bool> MergeAsync(RecipeProposal proposal, string overlayPath, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeOntologyWriter : IOntologyOverlayWriter
    {
        public Task<bool> MergeAsync(RecipeProposal proposal, string overlayPath, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static RecipeProposal FailurePriorProposal(RecipeProposalStatus status = RecipeProposalStatus.Pending)
    {
        var body = new FailurePriorBody
        {
            AgentName = "agent1",
            ConceptName = "McpToolError",
            AttributionPath = "agent1/search",
            ToolName = "search",
            FailureCount = 3,
            FirstSeen = DateTimeOffset.UtcNow.AddHours(-1),
            LastSeen = DateTimeOffset.UtcNow,
        };
        return new RecipeProposal
        {
            ProposalId = Guid.NewGuid().ToString("N"),
            Kind = RecipeProposalKind.FailurePrior,
            Concept = "McpToolError",
            Body = JsonSerializer.Serialize(body),
            Support = 3,
            Confidence = 0.0,
            SourceTraceIds = [],
            RiskLevel = RecipeProposalRiskLevel.Low,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static RecipeProposal TagSuggestionProposal() => new()
    {
        ProposalId = Guid.NewGuid().ToString("N"),
        Kind = RecipeProposalKind.TagSuggestion,
        Concept = "SearchTool",
        Body = "risk:Destructive",
        Support = 1,
        Confidence = 1.0,
        SourceTraceIds = [],
        RiskLevel = RecipeProposalRiskLevel.Low,
        Status = RecipeProposalStatus.Pending,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static OverlayPublishingRecipeProposalStoreDecorator MakeDecorator(
        RecipeProposal pendingProposal,
        IFailurePriorEvalGate? gate,
        FakeFailureWriter? failureWriter = null)
    {
        var inner = new InMemoryRecipeProposalStoreForTest(pendingProposal);
        var ontologyWriter = new FakeOntologyWriter();
        var fw = failureWriter ?? new FakeFailureWriter();

        return new OverlayPublishingRecipeProposalStoreDecorator(
            inner,
            ontologyWriter,
            overlayPath: "/tmp/fake.json",
            reloader: null,
            logger: null,
            throwOnSideEffectFailure: false,
            failureWriter: fw,
            failureOverlayPath: "/tmp/fake-failure.json",
            evalGate: gate);
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Gate_Reject_BlocksOverlayWrite()
    {
        var gate = new FakeGate { ReturnPassed = false, ReturnReason = "ungrounded" };
        var failureWriter = new FakeFailureWriter();
        var proposal = FailurePriorProposal();
        var decorator = MakeDecorator(proposal, gate, failureWriter);

        var result = await decorator.DecideAsync(proposal.ProposalId, approve: true, decidedBy: "op1");

        gate.CallCount.Should().Be(1);
        failureWriter.CallCount.Should().Be(0, "overlay write must be skipped when gate rejects");
        result!.Status.Should().Be(RecipeProposalStatus.Rejected);
    }

    [Fact]
    public async Task Gate_Accept_WritesOverlay()
    {
        var gate = new FakeGate { ReturnPassed = true };
        var failureWriter = new FakeFailureWriter();
        var proposal = FailurePriorProposal();
        var decorator = MakeDecorator(proposal, gate, failureWriter);

        var result = await decorator.DecideAsync(proposal.ProposalId, approve: true, decidedBy: "op1");

        gate.CallCount.Should().Be(1);
        failureWriter.CallCount.Should().Be(1, "overlay write must proceed when gate passes");
        result!.Status.Should().Be(RecipeProposalStatus.Approved);
    }

    [Fact]
    public async Task Gate_NonFailurePrior_SkipsGate()
    {
        var gate = new FakeGate();
        var proposal = TagSuggestionProposal();
        var decorator = MakeDecorator(proposal, gate);

        await decorator.DecideAsync(proposal.ProposalId, approve: true, decidedBy: "op1");

        gate.CallCount.Should().Be(0, "gate must not be called for non-FailurePrior kinds");
    }

    [Fact]
    public async Task Reject_Approve_False_SkipsGate()
    {
        var gate = new FakeGate();
        var proposal = FailurePriorProposal();
        var decorator = MakeDecorator(proposal, gate);

        await decorator.DecideAsync(proposal.ProposalId, approve: false, decidedBy: "op1");

        gate.CallCount.Should().Be(0, "gate must not be called when rejecting");
    }

    // ── inner store fake ──────────────────────────────────────────────────────

    private sealed class InMemoryRecipeProposalStoreForTest : IRecipeProposalStore
    {
        private RecipeProposal _current;

        public InMemoryRecipeProposalStoreForTest(RecipeProposal initial) => _current = initial;

        public ValueTask UpsertAsync(RecipeProposal proposal, CancellationToken cancellationToken = default)
        {
            _current = proposal;
            return ValueTask.CompletedTask;
        }

        public ValueTask<RecipeProposal?> GetAsync(string proposalId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<RecipeProposal?>(_current.ProposalId == proposalId ? _current : null);

        public IAsyncEnumerable<RecipeProposal> ListAsync(RecipeProposalQuery query, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<RecipeProposal>();

        public ValueTask<RecipeProposal?> DecideAsync(string proposalId, bool approve, string decidedBy, CancellationToken cancellationToken = default)
        {
            if (_current.ProposalId != proposalId) return ValueTask.FromResult<RecipeProposal?>(null);
            if (_current.Status != RecipeProposalStatus.Pending) return ValueTask.FromResult<RecipeProposal?>(_current);

            _current = _current with
            {
                Status = approve ? RecipeProposalStatus.Approved : RecipeProposalStatus.Rejected,
                ReviewedAt = DateTimeOffset.UtcNow,
                ReviewerId = decidedBy,
            };
            return ValueTask.FromResult<RecipeProposal?>(_current);
        }
    }
}
