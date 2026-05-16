// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Vais.Agents.Eval.Tests;

/// <summary>
/// EH-15: End-to-end regression flow test.
/// Assembles EvalRunRecord instances from scripted data, runs them through
/// the full assertion pipeline (factory registry → assertion eval → result),
/// and verifies all four assertion kinds behave correctly.
/// </summary>
public sealed class EvalRegressionFlowTests
{
    private static readonly EvalSuiteSpec SuiteSpec = new()
    {
        AgentId = "test-agent",
        Cases = new[]
        {
            new EvalCase
            {
                Id = "case-1",
                Input = "What tools did you call?",
                Assertions = new[]
                {
                    new EvalAssertion("no-turn-failed"),
                    new EvalAssertion("response-regex",  MakeParams(new { pattern = "\\d+" })),
                    new EvalAssertion("tool-call-sequence", MakeParams(new { expected = new[] { "search", "summarize" }, scoring = "f1" })),
                },
            },
            new EvalCase
            {
                Id = "case-2",
                Input = "Just respond.",
                Assertions = new[]
                {
                    new EvalAssertion("no-turn-failed"),
                    new EvalAssertion("response-regex", MakeParams(new { pattern = "hello", ignoreCase = true })),
                },
            },
        },
    };

    private static readonly EvalSuiteManifest Suite = new("regression-suite", "1.0")
    {
        Spec = SuiteSpec,
    };

    [Fact]
    public async Task RegressionFlow_EndToEnd_BothCases_AssertionsPass()
    {
        var registry = BuildRegistry();
        var caseCtx1 = new EvalCaseContext(SuiteSpec.Cases[0], Suite.Spec, AgentContext.Empty);
        var caseCtx2 = new EvalCaseContext(SuiteSpec.Cases[1], Suite.Spec, AgentContext.Empty);

        // Case 1: response "found 42 results", tools search + summarize called, no failure.
        var record1 = MakeRecord(
            responseText: "found 42 results",
            toolNames: new[] { "search", "summarize" },
            hasTurnFailed: false);

        var results1 = await EvaluateAllAsync(registry, caseCtx1, record1, SuiteSpec.Cases[0]);
        results1.Should().HaveCount(3);
        results1[0].Status.Should().Be(EvalAssertionStatus.Pass);  // no-turn-failed
        results1[1].Status.Should().Be(EvalAssertionStatus.Pass);  // response-regex \d+
        results1[2].Status.Should().Be(EvalAssertionStatus.Pass);  // tool-call-sequence F1=1.0

        // Case 2: response "Hello there", no tools, no failure.
        var record2 = MakeRecord(
            responseText: "Hello there",
            toolNames: Array.Empty<string>(),
            hasTurnFailed: false);

        var results2 = await EvaluateAllAsync(registry, caseCtx2, record2, SuiteSpec.Cases[1]);
        results2.Should().HaveCount(2);
        results2[0].Status.Should().Be(EvalAssertionStatus.Pass);  // no-turn-failed
        results2[1].Status.Should().Be(EvalAssertionStatus.Pass);  // response-regex hello (ignoreCase)
    }

    [Fact]
    public async Task ResponseRegex_Fail_WhenPatternDoesNotMatch()
    {
        var registry = BuildRegistry();
        var @case = SuiteSpec.Cases[0];
        var ctx = new EvalCaseContext(@case, Suite.Spec, AgentContext.Empty);
        var record = MakeRecord("no digits here!", Array.Empty<string>(), false);

        var assertionSpec = @case.Assertions.First(a => a.Kind == "response-regex");
        var factory = GetFactory(registry, assertionSpec.Kind);
        var assertion = factory.Create(assertionSpec.Params!.Value, NullServices);

        var result = await assertion.EvaluateAsync(ctx, record, default);

        result.Status.Should().Be(EvalAssertionStatus.Fail);
        result.Reason.Should().Contain("did not match");
    }

    [Fact]
    public async Task NoTurnFailed_Fail_WhenTurnFailedEventPresent()
    {
        var registry = BuildRegistry();
        var @case = SuiteSpec.Cases[0];
        var ctx = new EvalCaseContext(@case, Suite.Spec, AgentContext.Empty);
        var record = MakeRecord("response", Array.Empty<string>(), hasTurnFailed: true);

        var factory = GetFactory(registry, "no-turn-failed");
        var assertion = factory.Create(default, NullServices);

        var result = await assertion.EvaluateAsync(ctx, record, default);

        result.Status.Should().Be(EvalAssertionStatus.Fail);
        result.Reason.Should().Contain("Turn failed");
    }

    [Fact]
    public async Task ToolCallSequence_Exact_Fail_WhenOrderDiffers()
    {
        var registry = BuildRegistry();
        var @case = SuiteSpec.Cases[0];
        var ctx = new EvalCaseContext(@case, Suite.Spec, AgentContext.Empty);
        var record = MakeRecord("ok", new[] { "summarize", "search" }, false);

        var args = MakeParams(new { expected = new[] { "search", "summarize" }, scoring = "exact" });
        var factory = GetFactory(registry, "tool-call-sequence");
        var assertion = factory.Create(args, NullServices);

        var result = await assertion.EvaluateAsync(ctx, record, default);

        result.Status.Should().Be(EvalAssertionStatus.Fail);
        result.Score.Should().Be(0.0);
    }

    [Fact]
    public async Task ToolCallSequence_F1_PartialMatch_YieldsScore()
    {
        var registry = BuildRegistry();
        var @case = SuiteSpec.Cases[0];
        var ctx = new EvalCaseContext(@case, Suite.Spec, AgentContext.Empty);

        // actual has only "search", expected has "search" + "summarize"
        var record = MakeRecord("ok", new[] { "search" }, false);

        var args = MakeParams(new { expected = new[] { "search", "summarize" }, scoring = "f1" });
        var factory = GetFactory(registry, "tool-call-sequence");
        var assertion = factory.Create(args, NullServices);

        var result = await assertion.EvaluateAsync(ctx, record, default);

        result.Status.Should().Be(EvalAssertionStatus.Fail);
        result.Score.Should().BeApproximately(2.0 / 3.0, 1e-9);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IEvalAssertionFactoryRegistry BuildRegistry()
    {
        var services = new ServiceCollection();
        services.AddVaisAgentsEval();
        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IEvalAssertionFactoryRegistry>();
    }

    private static IEvalAssertionFactory GetFactory(IEvalAssertionFactoryRegistry registry, string kind)
    {
        registry.TryGet(kind, out var factory).Should().BeTrue($"factory '{kind}' must be registered");
        return factory!;
    }

    private static async Task<List<EvalAssertionResult>> EvaluateAllAsync(
        IEvalAssertionFactoryRegistry registry,
        EvalCaseContext ctx,
        EvalRunRecord record,
        EvalCase @case)
    {
        var results = new List<EvalAssertionResult>();
        foreach (var spec in @case.Assertions)
        {
            var factory = GetFactory(registry, spec.Kind);
            var assertion = factory.Create(spec.Params ?? default, NullServices);
            results.Add(await assertion.EvaluateAsync(ctx, record, default));
        }
        return results;
    }

    private static EvalRunRecord MakeRecord(string responseText, string[] toolNames, bool hasTurnFailed)
    {
        var journal = toolNames
            .Select(name => (JournalEntry)new ToolCallRecorded(
                "run-1", Guid.NewGuid().ToString("N"), name,
                default, new ToolCallOutcome("c1", "ok"), DateTimeOffset.UtcNow))
            .ToList();

        var events = new List<AgentEvent>();
        if (hasTurnFailed)
            events.Add(new TurnFailed(DateTimeOffset.UtcNow, AgentContext.Empty, "Exception", "test error", TimeSpan.Zero));

        return new EvalRunRecord(
            AgentRunId: "run-1",
            ResponseText: responseText,
            ResponseJson: null,
            JournalEntries: journal,
            Events: events,
            FinalState: null,
            Duration: TimeSpan.FromMilliseconds(100),
            PromptTokens: 10,
            CompletionTokens: 5);
    }

    private static JsonElement MakeParams(object obj)
        => JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(obj));

    private static readonly IServiceProvider NullServices =
        new ServiceCollection().BuildServiceProvider();
}
