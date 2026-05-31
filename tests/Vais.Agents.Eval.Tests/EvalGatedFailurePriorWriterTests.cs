// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Eval.Tests;

/// <summary>
/// FP-8 — <see cref="EvalGatedFailurePriorWriter"/> unit tests.
/// All tests use hand-rolled fakes (no mocking library).
/// </summary>
public sealed class EvalGatedFailurePriorWriterTests
{
    // ── fakes ────────────────────────────────────────────────────────────────

    private sealed class FakeRegistry : IEvalSuiteRegistry
    {
        private readonly List<EvalSuiteManifest> _suites = [];

        public void Add(EvalSuiteManifest suite) => _suites.Add(suite);

        public IAsyncEnumerable<EvalSuiteManifest> ListAsync(string? labelPrefix = null, CancellationToken ct = default)
            => _suites.ToAsyncEnumerable();

        public ValueTask<EvalSuiteManifest?> GetAsync(string id, string? version = null, CancellationToken ct = default)
            => ValueTask.FromResult(_suites.FirstOrDefault(s => s.Id == id));

        public ValueTask UpsertAsync(EvalSuiteManifest manifest, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask RemoveAsync(string id, string version, CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    private sealed class FakeLifecycle : IEvalRunLifecycleManager
    {
        private readonly Dictionary<string, EvalRunDetail> _details = new();
        private int _runCounter;

        public EvalRunStatus RunStatus { get; set; } = EvalRunStatus.Completed;
        public List<EvalCaseResultRecord> Cases { get; } = [];

        public ValueTask<string> StartRunAsync(string suiteName, string workspace, CancellationToken ct = default)
        {
            var runId = $"run-{++_runCounter}";
            var summary = new EvalRunSummary(runId, suiteName, "1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                RunStatus, Cases.Count, 0, 0);
            _details[runId] = new EvalRunDetail(summary, Cases);
            return ValueTask.FromResult(runId);
        }

        public ValueTask CancelRunAsync(string evalRunId, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<EvalRunDetail?> GetRunDetailAsync(string evalRunId, CancellationToken ct = default)
            => ValueTask.FromResult(_details.TryGetValue(evalRunId, out var d) ? d : (EvalRunDetail?)null);

        public ValueTask<IReadOnlyList<EvalRunSummary>> ListRunsAsync(string? suiteName = null, int limit = 50,
            string? source = null, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<EvalRunSummary>>([]);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static EvalSuiteManifest Suite(string agentId) => new EvalSuiteManifest(
        Id: $"suite-{agentId}",
        Version: "1")
    {
        Spec = new EvalSuiteSpec { AgentId = agentId }
    };

    private static EvalSuiteManifest SuiteViaTarget(string agentRef) => new EvalSuiteManifest(
        Id: $"suite-{agentRef}",
        Version: "1")
    {
        Spec = new EvalSuiteSpec { Target = new EvalTarget { AgentRef = agentRef } }
    };

    private static RecipeProposal FailurePriorProposal(string agentName, string concept = "McpToolError")
    {
        var body = new FailurePriorBody
        {
            AgentName = agentName,
            ConceptName = concept,
            AttributionPath = $"{agentName}/search",
            ToolName = "search",
            FailureCount = 3,
            FirstSeen = DateTimeOffset.UtcNow.AddHours(-1),
            LastSeen = DateTimeOffset.UtcNow,
        };
        return new RecipeProposal
        {
            ProposalId = Guid.NewGuid().ToString("N"),
            Kind = RecipeProposalKind.FailurePrior,
            Concept = concept,
            Body = JsonSerializer.Serialize(body),
            Support = 3,
            Confidence = 0.0,
            SourceTraceIds = [],
            RiskLevel = RecipeProposalRiskLevel.Low,
            Status = RecipeProposalStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static EvalCaseResultRecord CaseResult(int mechanicalCount) => new(
        EvalRunId: "run-1",
        CaseId: "case-1",
        AgentRunId: null,
        StartedAt: DateTimeOffset.UtcNow,
        CompletedAt: DateTimeOffset.UtcNow,
        Status: EvalCaseStatus.Pass,
        ResponseText: null,
        AssertionResults: [],
        MechanicalFailureCount: mechanicalCount);

    private static EvalGatedFailurePriorWriter Gate(
        FakeRegistry registry,
        FakeLifecycle lifecycle,
        TimeSpan? timeout = null)
        => new(registry, lifecycle, workspace: "default", timeout: timeout ?? TimeSpan.FromSeconds(30));

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Gate_NoMatchingSuite_Passes()
    {
        var registry = new FakeRegistry(); // empty
        var lifecycle = new FakeLifecycle();
        var gate = Gate(registry, lifecycle);

        var (passed, reason) = await gate.EvaluateAsync(FailurePriorProposal("agent1"), default);

        passed.Should().BeTrue();
        reason.Should().BeNull();
    }

    [Fact]
    public async Task Gate_SuiteFoundByAgentId_HasMechanicalFailures_Passes()
    {
        var registry = new FakeRegistry();
        registry.Add(Suite("agent1"));
        var lifecycle = new FakeLifecycle();
        lifecycle.Cases.Add(CaseResult(2));

        var (passed, reason) = await Gate(registry, lifecycle).EvaluateAsync(FailurePriorProposal("agent1"), default);

        passed.Should().BeTrue();
        reason.Should().BeNull();
    }

    [Fact]
    public async Task Gate_SuiteFoundByTargetAgentRef_HasMechanicalFailures_Passes()
    {
        var registry = new FakeRegistry();
        registry.Add(SuiteViaTarget("agent2"));
        var lifecycle = new FakeLifecycle();
        lifecycle.Cases.Add(CaseResult(1));

        var (passed, reason) = await Gate(registry, lifecycle).EvaluateAsync(FailurePriorProposal("agent2"), default);

        passed.Should().BeTrue();
        reason.Should().BeNull();
    }

    [Fact]
    public async Task Gate_SuiteFound_ZeroMechanicalFailures_Rejects()
    {
        var registry = new FakeRegistry();
        registry.Add(Suite("agent3"));
        var lifecycle = new FakeLifecycle();
        lifecycle.Cases.Add(CaseResult(0));
        lifecycle.Cases.Add(CaseResult(0));
        lifecycle.Cases.Add(CaseResult(0));

        var (passed, reason) = await Gate(registry, lifecycle).EvaluateAsync(FailurePriorProposal("agent3"), default);

        passed.Should().BeFalse();
        reason.Should().NotBeNullOrEmpty();
        reason.Should().Contain("suite-agent3");
        reason.Should().Contain("agent3");
    }

    [Fact]
    public async Task Gate_BodyParseFailure_Passes()
    {
        var registry = new FakeRegistry();
        registry.Add(Suite("agent1"));
        var lifecycle = new FakeLifecycle();

        var broken = new RecipeProposal
        {
            ProposalId = "x",
            Kind = RecipeProposalKind.FailurePrior,
            Concept = "McpToolError",
            Body = "not-valid-json",
            Support = 1,
            Confidence = 0.0,
            SourceTraceIds = [],
            RiskLevel = RecipeProposalRiskLevel.Low,
            Status = RecipeProposalStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var (passed, reason) = await Gate(registry, lifecycle).EvaluateAsync(broken, default);

        passed.Should().BeTrue();
        reason.Should().Be("body-parse-failed");
    }

    [Fact]
    public async Task Gate_RunTimesOut_FailsOpen()
    {
        var registry = new FakeRegistry();
        registry.Add(Suite("agent4"));

        // Lifecycle that always returns Running status.
        var lifecycle = new NeverCompletesLifecycle();
        var gate = new EvalGatedFailurePriorWriter(registry, lifecycle, workspace: "default",
            timeout: TimeSpan.FromMilliseconds(50));

        var (passed, reason) = await gate.EvaluateAsync(FailurePriorProposal("agent4"), default);

        passed.Should().BeTrue();
        reason.Should().Be("eval-gate-timeout");
    }

    [Fact]
    public async Task Gate_RunFailed_FailsOpen()
    {
        var registry = new FakeRegistry();
        registry.Add(Suite("agent5"));
        var lifecycle = new FakeLifecycle { RunStatus = EvalRunStatus.Failed };

        var (passed, reason) = await Gate(registry, lifecycle).EvaluateAsync(FailurePriorProposal("agent5"), default);

        passed.Should().BeTrue();
        reason.Should().Be("eval-run-did-not-complete");
    }

    [Fact]
    public async Task Gate_RunCancelled_FailsOpen()
    {
        var registry = new FakeRegistry();
        registry.Add(Suite("agent6"));
        var lifecycle = new FakeLifecycle { RunStatus = EvalRunStatus.Cancelled };

        var (passed, reason) = await Gate(registry, lifecycle).EvaluateAsync(FailurePriorProposal("agent6"), default);

        passed.Should().BeTrue();
        reason.Should().Be("eval-run-did-not-complete");
    }

    // ── helper fake for timeout test ──────────────────────────────────────────

    private sealed class NeverCompletesLifecycle : IEvalRunLifecycleManager
    {
        public ValueTask<string> StartRunAsync(string suiteName, string workspace, CancellationToken ct = default)
            => ValueTask.FromResult("run-never");

        public ValueTask CancelRunAsync(string evalRunId, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<EvalRunDetail?> GetRunDetailAsync(string evalRunId, CancellationToken ct = default)
        {
            var summary = new EvalRunSummary(evalRunId, "s", "1", DateTimeOffset.UtcNow, null,
                EvalRunStatus.Running, 1, 0, 0);
            return ValueTask.FromResult<EvalRunDetail?>(new EvalRunDetail(summary, []));
        }

        public ValueTask<IReadOnlyList<EvalRunSummary>> ListRunsAsync(string? suiteName = null, int limit = 50,
            string? source = null, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<EvalRunSummary>>([]);
    }
}

// ── async enumerable helper ───────────────────────────────────────────────────

file static class AsyncEnumerableExtensions
{
    public static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(this IEnumerable<T> source)
    {
        foreach (var item in source)
        {
            yield return item;
            await Task.Yield();
        }
    }
}
