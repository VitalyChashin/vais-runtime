// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Vais.Agents.Eval.Tests;

/// <summary>
/// EH-21: Unit tests for the 6 E3 assertion kinds:
/// response-json-schema, metric-threshold, no-guardrail-deny,
/// graph-final-state, expect-interrupt, custom.
/// </summary>
public sealed class EvalAssertionE3Tests
{
    private static readonly EvalSuiteSpec SuiteSpec = new() { AgentId = "test", Cases = Array.Empty<EvalCase>() };
    private static readonly EvalCase DummyCase = new() { Id = "c1", Input = "x", Assertions = Array.Empty<EvalAssertion>() };
    private static readonly EvalCaseContext Ctx = new(DummyCase, SuiteSpec, AgentContext.Empty);

    // ── response-json-schema ─────────────────────────────────────────────────

    [Fact]
    public async Task ResponseJsonSchema_Pass_WhenJsonIsValidAndRequiredFieldPresent()
    {
        var factory = GetFactory("response-json-schema");
        var args = MakeParams(new { schema = new { type = "object", required = new[] { "name" } } });
        var assertion = factory.Create(args, NullServices);
        var record = MakeRecord(responseText: """{"name":"Alice"}""");

        var result = await assertion.EvaluateAsync(Ctx, record, default);

        result.Status.Should().Be(EvalAssertionStatus.Pass);
    }

    [Fact]
    public async Task ResponseJsonSchema_Fail_WhenRequiredFieldMissing()
    {
        var factory = GetFactory("response-json-schema");
        var args = MakeParams(new { schema = new { type = "object", required = new[] { "name" } } });
        var assertion = factory.Create(args, NullServices);
        var record = MakeRecord(responseText: """{"age":30}""");

        var result = await assertion.EvaluateAsync(Ctx, record, default);

        result.Status.Should().Be(EvalAssertionStatus.Fail);
        result.Reason.Should().Contain("name");
    }

    [Fact]
    public async Task ResponseJsonSchema_Fail_WhenResponseIsNotJson()
    {
        var factory = GetFactory("response-json-schema");
        var args = MakeParams(new { schema = new { type = "object" } });
        var assertion = factory.Create(args, NullServices);
        var record = MakeRecord(responseText: "not json at all");

        var result = await assertion.EvaluateAsync(Ctx, record, default);

        result.Status.Should().Be(EvalAssertionStatus.Fail);
    }

    [Fact]
    public async Task ResponseJsonSchema_Fail_WhenRootTypeIsWrong()
    {
        var factory = GetFactory("response-json-schema");
        var args = MakeParams(new { schema = new { type = "object" } });
        var assertion = factory.Create(args, NullServices);
        var record = MakeRecord(responseText: """[1,2,3]""");

        var result = await assertion.EvaluateAsync(Ctx, record, default);

        result.Status.Should().Be(EvalAssertionStatus.Fail);
        result.Reason.Should().Contain("object");
    }

    // ── metric-threshold ─────────────────────────────────────────────────────

    [Fact]
    public async Task MetricThreshold_Pass_WhenDurationBelowMax()
    {
        var factory = GetFactory("metric-threshold");
        var args = MakeParams(new { metric = "duration", max = 5000 }); // 5000 ms
        var assertion = factory.Create(args, NullServices);
        var record = MakeRecord(duration: TimeSpan.FromMilliseconds(100));

        var result = await assertion.EvaluateAsync(Ctx, record, default);

        result.Status.Should().Be(EvalAssertionStatus.Pass);
    }

    [Fact]
    public async Task MetricThreshold_Fail_WhenDurationExceedsMax()
    {
        var factory = GetFactory("metric-threshold");
        var args = MakeParams(new { metric = "duration", max = 50 }); // 50 ms
        var assertion = factory.Create(args, NullServices);
        var record = MakeRecord(duration: TimeSpan.FromMilliseconds(1000));

        var result = await assertion.EvaluateAsync(Ctx, record, default);

        result.Status.Should().Be(EvalAssertionStatus.Fail);
    }

    [Fact]
    public async Task MetricThreshold_Pass_WhenTotalTokensBelowMax()
    {
        var factory = GetFactory("metric-threshold");
        var args = MakeParams(new { metric = "totalTokens", max = 100 });
        var assertion = factory.Create(args, NullServices);
        var record = MakeRecord(promptTokens: 30, completionTokens: 20);

        var result = await assertion.EvaluateAsync(Ctx, record, default);

        result.Status.Should().Be(EvalAssertionStatus.Pass);
        result.Score.Should().Be(1.0);
    }

    [Fact]
    public async Task MetricThreshold_Skipped_WhenTokensUnavailable()
    {
        var factory = GetFactory("metric-threshold");
        var args = MakeParams(new { metric = "promptTokens", max = 100 });
        var assertion = factory.Create(args, NullServices);
        var record = MakeRecord(); // no token data

        var result = await assertion.EvaluateAsync(Ctx, record, default);

        result.Status.Should().Be(EvalAssertionStatus.Skipped);
    }

    [Fact]
    public async Task MetricThreshold_Pass_WhenToolCallCountBelowMax()
    {
        var factory = GetFactory("metric-threshold");
        var args = MakeParams(new { metric = "toolCalls.count", max = 5 });
        var assertion = factory.Create(args, NullServices);
        var record = MakeRecord(toolNames: new[] { "tool1", "tool2" });

        var result = await assertion.EvaluateAsync(Ctx, record, default);

        result.Status.Should().Be(EvalAssertionStatus.Pass);
        result.Score.Should().Be(1.0);
    }

    // ── no-guardrail-deny ────────────────────────────────────────────────────

    [Fact]
    public async Task NoGuardrailDeny_Pass_WhenNoGuardrailEvents()
    {
        var factory = GetFactory("no-guardrail-deny");
        var assertion = factory.Create(default, NullServices);
        var record = MakeRecord();

        var result = await assertion.EvaluateAsync(Ctx, record, default);

        result.Status.Should().Be(EvalAssertionStatus.Pass);
    }

    [Fact]
    public async Task NoGuardrailDeny_Pass_WhenGuardrailAllowedNotDenied()
    {
        var factory = GetFactory("no-guardrail-deny");
        var assertion = factory.Create(default, NullServices);
        var events = new List<AgentEvent>
        {
            new GuardrailTriggered(DateTimeOffset.UtcNow, AgentContext.Empty, GuardrailLayer.Input, GuardrailDecision.Pass, "ok"),
        };
        var record = MakeRecord(events: events);

        var result = await assertion.EvaluateAsync(Ctx, record, default);

        result.Status.Should().Be(EvalAssertionStatus.Pass);
    }

    [Fact]
    public async Task NoGuardrailDeny_Fail_WhenGuardrailDenied()
    {
        var factory = GetFactory("no-guardrail-deny");
        var assertion = factory.Create(default, NullServices);
        var events = new List<AgentEvent>
        {
            new GuardrailTriggered(DateTimeOffset.UtcNow, AgentContext.Empty, GuardrailLayer.Tool, GuardrailDecision.Deny, "blocked"),
        };
        var record = MakeRecord(events: events);

        var result = await assertion.EvaluateAsync(Ctx, record, default);

        result.Status.Should().Be(EvalAssertionStatus.Fail);
        result.Reason.Should().ContainEquivalentOf("deny");
    }

    [Fact]
    public async Task NoGuardrailDeny_Pass_WhenDenyOnDifferentLayer()
    {
        var factory = GetFactory("no-guardrail-deny");
        var args = MakeParams(new { layer = "Tool" });
        var assertion = factory.Create(args, NullServices);
        // Deny on Llm layer — assertion filters to Tool only, so should pass
        var events = new List<AgentEvent>
        {
            new GuardrailTriggered(DateTimeOffset.UtcNow, AgentContext.Empty, GuardrailLayer.Input, GuardrailDecision.Deny, "blocked"),
        };
        var record = MakeRecord(events: events);

        var result = await assertion.EvaluateAsync(Ctx, record, default);

        result.Status.Should().Be(EvalAssertionStatus.Pass);
    }

    // ── graph-final-state ────────────────────────────────────────────────────

    [Fact]
    public async Task GraphFinalState_Skipped_WhenFinalStateIsNull()
    {
        var factory = GetFactory("graph-final-state");
        var args = MakeParams(new { path = "$.status", op = "equals", value = "done" });
        var assertion = factory.Create(args, NullServices);
        var record = MakeRecord(); // FinalState = null

        var result = await assertion.EvaluateAsync(Ctx, record, default);

        result.Status.Should().Be(EvalAssertionStatus.Skipped);
    }

    [Fact]
    public async Task GraphFinalState_Pass_WhenPathEqualsValue()
    {
        var factory = GetFactory("graph-final-state");
        var args = MakeParams(new { path = "$.status", op = "equals", value = "done" });
        var assertion = factory.Create(args, NullServices);
        var record = MakeRecord(finalState: MakeFinalState("""{"status":"done"}"""));

        var result = await assertion.EvaluateAsync(Ctx, record, default);

        result.Status.Should().Be(EvalAssertionStatus.Pass);
    }

    [Fact]
    public async Task GraphFinalState_Fail_WhenPathValueMismatch()
    {
        var factory = GetFactory("graph-final-state");
        var args = MakeParams(new { path = "$.status", op = "equals", value = "done" });
        var assertion = factory.Create(args, NullServices);
        var record = MakeRecord(finalState: MakeFinalState("""{"status":"pending"}"""));

        var result = await assertion.EvaluateAsync(Ctx, record, default);

        result.Status.Should().Be(EvalAssertionStatus.Fail);
    }

    [Fact]
    public async Task GraphFinalState_Pass_WhenContainsOp()
    {
        var factory = GetFactory("graph-final-state");
        var args = MakeParams(new { path = "$.message", op = "contains", value = "success" });
        var assertion = factory.Create(args, NullServices);
        var record = MakeRecord(finalState: MakeFinalState("""{"message":"operation success complete"}"""));

        var result = await assertion.EvaluateAsync(Ctx, record, default);

        result.Status.Should().Be(EvalAssertionStatus.Pass);
    }

    // ── expect-interrupt ─────────────────────────────────────────────────────

    [Fact]
    public async Task ExpectInterrupt_Pass_WhenInterruptPresent()
    {
        var factory = GetFactory("expect-interrupt");
        var assertion = factory.Create(default, NullServices);
        var events = new List<AgentEvent>
        {
            new InterruptRaised(DateTimeOffset.UtcNow, AgentContext.Empty, "irq-1", "needs approval"),
        };
        var record = MakeRecord(events: events);

        var result = await assertion.EvaluateAsync(Ctx, record, default);

        result.Status.Should().Be(EvalAssertionStatus.Pass);
    }

    [Fact]
    public async Task ExpectInterrupt_Fail_WhenNoInterrupt()
    {
        var factory = GetFactory("expect-interrupt");
        var assertion = factory.Create(default, NullServices);
        var record = MakeRecord();

        var result = await assertion.EvaluateAsync(Ctx, record, default);

        result.Status.Should().Be(EvalAssertionStatus.Fail);
    }

    [Fact]
    public async Task ExpectInterrupt_Pass_WhenMatchingInterruptId()
    {
        var factory = GetFactory("expect-interrupt");
        var args = MakeParams(new { interruptId = "irq-1" });
        var assertion = factory.Create(args, NullServices);
        var events = new List<AgentEvent>
        {
            new InterruptRaised(DateTimeOffset.UtcNow, AgentContext.Empty, "irq-1", "needs approval"),
        };
        var record = MakeRecord(events: events);

        var result = await assertion.EvaluateAsync(Ctx, record, default);

        result.Status.Should().Be(EvalAssertionStatus.Pass);
    }

    [Fact]
    public async Task ExpectInterrupt_Fail_WhenDifferentInterruptId()
    {
        var factory = GetFactory("expect-interrupt");
        var args = MakeParams(new { interruptId = "irq-expected" });
        var assertion = factory.Create(args, NullServices);
        var events = new List<AgentEvent>
        {
            new InterruptRaised(DateTimeOffset.UtcNow, AgentContext.Empty, "irq-other", "needs approval"),
        };
        var record = MakeRecord(events: events);

        var result = await assertion.EvaluateAsync(Ctx, record, default);

        result.Status.Should().Be(EvalAssertionStatus.Fail);
    }

    // ── custom ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Custom_Delegates_ToRegisteredFactory()
    {
        var services = BuildServices();
        var registry = services.GetRequiredService<IEvalAssertionFactoryRegistry>();

        // Use an existing built-in assertion as the delegate target.
        registry.TryGet("custom", out var customFactory).Should().BeTrue();
        var args = MakeParams(new { name = "no-turn-failed" });
        var assertion = customFactory!.Create(args, services);
        var record = MakeRecord(); // no failure events

        var result = await assertion.EvaluateAsync(Ctx, record, default);

        result.Status.Should().Be(EvalAssertionStatus.Pass);
    }

    [Fact]
    public async Task Custom_ReturnsError_WhenNamedKindNotRegistered()
    {
        var services = BuildServices();
        var registry = services.GetRequiredService<IEvalAssertionFactoryRegistry>();

        registry.TryGet("custom", out var customFactory).Should().BeTrue();
        var args = MakeParams(new { name = "does-not-exist" });
        var assertion = customFactory!.Create(args, services);

        var result = await assertion.EvaluateAsync(Ctx, MakeRecord(), default);

        result.Status.Should().Be(EvalAssertionStatus.Error);
        result.Reason.Should().Contain("does-not-exist");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IServiceProvider BuildServices()
    {
        var sc = new ServiceCollection();
        sc.AddVaisAgentsEval();
        return sc.BuildServiceProvider();
    }

    private static IEvalAssertionFactory GetFactory(string kind)
    {
        var registry = BuildServices().GetRequiredService<IEvalAssertionFactoryRegistry>();
        registry.TryGet(kind, out var factory).Should().BeTrue($"factory '{kind}' must be registered");
        return factory!;
    }

    private static EvalRunRecord MakeRecord(
        string responseText = "ok",
        string[]? toolNames = null,
        List<AgentEvent>? events = null,
        TimeSpan? duration = null,
        int? promptTokens = null,
        int? completionTokens = null,
        IReadOnlyDictionary<string, JsonElement>? finalState = null)
    {
        var journal = (toolNames ?? Array.Empty<string>())
            .Select(name => (JournalEntry)new ToolCallRecorded(
                "run-1", Guid.NewGuid().ToString("N"), name,
                default, new ToolCallOutcome("c1", "ok"), DateTimeOffset.UtcNow))
            .ToList();

        return new EvalRunRecord(
            AgentRunId: "run-1",
            ResponseText: responseText,
            ResponseJson: null,
            JournalEntries: journal,
            Events: events ?? new List<AgentEvent>(),
            FinalState: finalState,
            Duration: duration ?? TimeSpan.FromMilliseconds(100),
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens);
    }

    private static IReadOnlyDictionary<string, JsonElement> MakeFinalState(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private static JsonElement MakeParams(object obj)
        => JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(obj));

    private static readonly IServiceProvider NullServices =
        new ServiceCollection().BuildServiceProvider();
}
