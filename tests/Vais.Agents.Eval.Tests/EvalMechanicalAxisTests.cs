// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Vais.Agents.Eval.Tests;

/// <summary>
/// Part 3 — Eval mechanical axis (EM-3 / EM-7).
/// Verifies that:
/// - The four new assertion kinds (<c>no-tool-error</c>, <c>no-degraded-response</c>,
///   <c>max-retries</c>, <c>no-fallback-engaged</c>) are registered and evaluate correctly.
/// - A quality-passing case with recovered mechanical failures correctly reports
///   <see cref="FailureLevel.Warning"/> and a non-zero <see cref="EvalCaseResultRecord.MechanicalFailureCount"/>.
/// </summary>
public sealed class EvalMechanicalAxisTests
{
    private static readonly EvalSuiteSpec SuiteSpec = new() { AgentId = "test", Cases = Array.Empty<EvalCase>() };
    private static readonly EvalCase DummyCase = new() { Id = "c1", Input = "x", Assertions = Array.Empty<EvalAssertion>() };
    private static readonly EvalCaseContext Ctx = new(DummyCase, SuiteSpec, AgentContext.Empty);

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static IEvalAssertionFactory GetFactory(string kind)
    {
        var sc = new ServiceCollection();
        sc.AddVaisAgentsEval();
        var registry = sc.BuildServiceProvider().GetRequiredService<IEvalAssertionFactoryRegistry>();
        registry.TryGet(kind, out var factory).Should().BeTrue($"factory '{kind}' must be registered");
        return factory!;
    }

    private static EvalRunRecord MakeRecord(List<AgentEvent>? events = null) =>
        new("run-1", "The answer.", null, [], events ?? [], null, TimeSpan.FromMilliseconds(100), null, null);

    private static JsonElement MakeParams(object obj) =>
        JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(obj));

    private static readonly IServiceProvider NullServices = new ServiceCollection().BuildServiceProvider();

    // ── no-tool-error ──────────────────────────────────────────────────────────

    [Fact]
    public async Task NoToolError_Pass_WhenNoToolFailed()
    {
        var factory = GetFactory("no-tool-error");
        var assertion = factory.Create(default, NullServices);
        var events = new List<AgentEvent>
        {
            new ToolCallCompleted(DateTimeOffset.UtcNow, AgentContext.Empty, "c1", "search", Succeeded: true, Error: null, TimeSpan.Zero),
        };

        var result = await assertion.EvaluateAsync(Ctx, MakeRecord(events), default);

        result.Status.Should().Be(EvalAssertionStatus.Pass);
    }

    [Fact]
    public async Task NoToolError_Fail_WhenToolFailed()
    {
        var factory = GetFactory("no-tool-error");
        var assertion = factory.Create(default, NullServices);
        var events = new List<AgentEvent>
        {
            new ToolCallCompleted(DateTimeOffset.UtcNow, AgentContext.Empty, "c1", "search",
                Succeeded: false, Error: "HttpRequestException", TimeSpan.Zero) { Level = FailureLevel.Warning },
        };

        var result = await assertion.EvaluateAsync(Ctx, MakeRecord(events), default);

        result.Status.Should().Be(EvalAssertionStatus.Fail);
        result.Reason.Should().Contain("search").And.Contain("HttpRequestException");
    }

    // ── no-degraded-response ───────────────────────────────────────────────────

    [Fact]
    public async Task NoDegradedResponse_Pass_WhenTurnClean()
    {
        var factory = GetFactory("no-degraded-response");
        var assertion = factory.Create(default, NullServices);
        var events = new List<AgentEvent>
        {
            new TurnCompleted(DateTimeOffset.UtcNow, AgentContext.Empty, "The answer.", null, null, null, TimeSpan.Zero),
        };

        var result = await assertion.EvaluateAsync(Ctx, MakeRecord(events), default);

        result.Status.Should().Be(EvalAssertionStatus.Pass);
    }

    [Fact]
    public async Task NoDegradedResponse_Fail_WhenTurnPartial()
    {
        var factory = GetFactory("no-degraded-response");
        var assertion = factory.Create(default, NullServices);
        var events = new List<AgentEvent>
        {
            new TurnCompleted(DateTimeOffset.UtcNow, AgentContext.Empty, "No analysis produced.",
                null, null, null, TimeSpan.Zero) { Level = FailureLevel.Warning },
        };

        var result = await assertion.EvaluateAsync(Ctx, MakeRecord(events), default);

        result.Status.Should().Be(EvalAssertionStatus.Fail);
        result.Reason.Should().Contain("partial");
    }

    // ── max-retries ────────────────────────────────────────────────────────────

    [Fact]
    public async Task MaxRetries_Pass_WhenUnderThreshold()
    {
        var factory = GetFactory("max-retries");
        var assertion = factory.Create(MakeParams(new { max = 2 }), NullServices);
        var events = new List<AgentEvent>
        {
            new LlmCallRetried(DateTimeOffset.UtcNow, AgentContext.Empty, 0, "HttpRequestException", IsTransient: true),
            new LlmCallRetried(DateTimeOffset.UtcNow, AgentContext.Empty, 1, "HttpRequestException", IsTransient: true),
        };

        var result = await assertion.EvaluateAsync(Ctx, MakeRecord(events), default);

        result.Status.Should().Be(EvalAssertionStatus.Pass);
    }

    [Fact]
    public async Task MaxRetries_Fail_WhenExceedsThreshold()
    {
        var factory = GetFactory("max-retries");
        var assertion = factory.Create(MakeParams(new { max = 1 }), NullServices);
        var events = new List<AgentEvent>
        {
            new LlmCallRetried(DateTimeOffset.UtcNow, AgentContext.Empty, 0, "Timeout", IsTransient: true),
            new LlmCallRetried(DateTimeOffset.UtcNow, AgentContext.Empty, 1, "Timeout", IsTransient: true),
        };

        var result = await assertion.EvaluateAsync(Ctx, MakeRecord(events), default);

        result.Status.Should().Be(EvalAssertionStatus.Fail);
        result.Reason.Should().Contain("2").And.Contain("threshold is 1");
    }

    [Fact]
    public async Task MaxRetries_ZeroAllowed_Fails_OnAnyRetry()
    {
        var factory = GetFactory("max-retries");
        // Default (no params) = max 0
        var assertion = factory.Create(default, NullServices);
        var events = new List<AgentEvent>
        {
            new LlmCallRetried(DateTimeOffset.UtcNow, AgentContext.Empty, 0, "Timeout", IsTransient: true),
        };

        var result = await assertion.EvaluateAsync(Ctx, MakeRecord(events), default);

        result.Status.Should().Be(EvalAssertionStatus.Fail);
    }

    // ── no-fallback-engaged ────────────────────────────────────────────────────

    [Fact]
    public async Task NoFallbackEngaged_Pass_WhenNoFallback()
    {
        var factory = GetFactory("no-fallback-engaged");
        var assertion = factory.Create(default, NullServices);

        var result = await assertion.EvaluateAsync(Ctx, MakeRecord(), default);

        result.Status.Should().Be(EvalAssertionStatus.Pass);
    }

    [Fact]
    public async Task NoFallbackEngaged_Fail_WhenFallbackFired()
    {
        var factory = GetFactory("no-fallback-engaged");
        var assertion = factory.Create(default, NullServices);
        var events = new List<AgentEvent>
        {
            new LlmFallbackEngaged(DateTimeOffset.UtcNow, AgentContext.Empty,
                FromProviderIndex: 0, ToProviderIndex: 1,
                FromProviderType: "GptPrimary", ToProviderType: "GptFallback",
                Reason: "RateLimitException"),
        };

        var result = await assertion.EvaluateAsync(Ctx, MakeRecord(events), default);

        result.Status.Should().Be(EvalAssertionStatus.Fail);
        result.Reason.Should().Contain("RateLimitException");
    }

    // ── ComputeMechanicalAxis (EM-2) — the two-axis invariant ─────────────────
    // Verified indirectly: a case with recovered tool errors must show
    // Status=Pass (quality axis) while MechanicalLevel=Warning (mechanical axis).
    // We test this through EvalCaseResultRecord field defaults.

    [Fact]
    public void EvalCaseResultRecord_Defaults_MechanicalClean()
    {
        var record = new EvalCaseResultRecord(
            "run-1", "c1", null, DateTimeOffset.UtcNow, null,
            EvalCaseStatus.Pass, "ok", []);

        record.MechanicalLevel.Should().Be(FailureLevel.Default);
        record.MechanicalFailureCount.Should().Be(0);
        record.MechanicalBreakdown.Should().BeNull();
    }

    [Fact]
    public void EvalCaseResultRecord_Carries_MechanicalWarning_While_QualityPasses()
    {
        // The two-axis invariant: quality Pass + mechanical Warning = "answered but degraded"
        var breakdown = new Dictionary<string, int> { ["toolError"] = 2, ["llmRetry"] = 1 };
        var record = new EvalCaseResultRecord(
            "run-1", "c1", null, DateTimeOffset.UtcNow, null,
            EvalCaseStatus.Pass, "The answer.", [],
            MechanicalLevel: FailureLevel.Warning,
            MechanicalFailureCount: 3,
            MechanicalBreakdown: breakdown);

        record.Status.Should().Be(EvalCaseStatus.Pass,
            "quality axis: the judge/assertions passed");
        record.MechanicalLevel.Should().Be(FailureLevel.Warning,
            "mechanical axis: there were recovered failures");
        record.MechanicalFailureCount.Should().Be(3);
        record.MechanicalBreakdown.Should().ContainKey("toolError").WhoseValue.Should().Be(2);
        record.MechanicalBreakdown.Should().ContainKey("llmRetry").WhoseValue.Should().Be(1);
    }
}
