// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Observability.RunHealthStore;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// Part 2a — subscriber concept stamping (FT-5, exit criterion #2).
/// Tests that RunHealthSignalSubscriber.Map returns a record whose Kind
/// maps to the expected ConceptName via AutoDerivedFailureOntologyCatalog.
/// This validates the FT-5 wiring (subscriber stamps ConceptName via catalog.FromSignalKind)
/// without needing Postgres or an event bus.
/// </summary>
public sealed class RunHealthSubscriberStampTests
{
    private static readonly DateTimeOffset _at = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly AgentContext _ctx = AgentContext.Empty with { RunId = "run-1" };

    private static string? ExpectedConcept(RunHealthSignalKind kind) =>
        AutoDerivedFailureOntologyCatalog.Instance.FromSignalKind(kind)?.Name;

    // ── Map → ConceptName wiring ─────────────────────────────────────────────────

    [Fact]
    public void Map_ToolError_MapsToToolErrorConcept()
    {
        var evt = new ToolCallCompleted(_at, _ctx, "c1", "search", Succeeded: false, Error: "err", TimeSpan.Zero);
        var record = RunHealthSignalSubscriber.Map(evt)!;
        record.Kind.Should().Be(RunHealthSignalKind.ToolError);
        ExpectedConcept(record.Kind).Should().Be("ToolError");
    }

    [Fact]
    public void Map_TurnFailed_MapsToTurnFailedConcept()
    {
        var evt = new TurnFailed(_at, _ctx, "System.Exception", "boom", TimeSpan.Zero);
        var record = RunHealthSignalSubscriber.Map(evt)!;
        record.Kind.Should().Be(RunHealthSignalKind.TurnFailed);
        ExpectedConcept(record.Kind).Should().Be("TurnFailed");
    }

    [Fact]
    public void Map_LlmCallRetried_MapsToLlmCallRetriedConcept()
    {
        var evt = new LlmCallRetried(_at, _ctx, AttemptIndex: 1, ErrorType: "HttpRequestException", IsTransient: true);
        var record = RunHealthSignalSubscriber.Map(evt)!;
        record.Kind.Should().Be(RunHealthSignalKind.LlmRetry);
        ExpectedConcept(record.Kind).Should().Be("LlmCallRetried");
    }

    [Fact]
    public void Map_LlmFallbackEngaged_MapsToLlmFallbackEngagedConcept()
    {
        var evt = new LlmFallbackEngaged(_at, _ctx, 0, 1, null, null, "primary down");
        var record = RunHealthSignalSubscriber.Map(evt)!;
        record.Kind.Should().Be(RunHealthSignalKind.LlmFallback);
        ExpectedConcept(record.Kind).Should().Be("LlmFallbackEngaged");
    }

    [Fact]
    public void Map_GuardrailTriggered_MapsToGuardrailTriggeredConcept()
    {
        var evt = new GuardrailTriggered(_at, _ctx, GuardrailLayer.Output, GuardrailDecision.Deny, "pii detected");
        var record = RunHealthSignalSubscriber.Map(evt)!;
        record.Kind.Should().Be(RunHealthSignalKind.Guardrail);
        ExpectedConcept(record.Kind).Should().Be("GuardrailTriggered");
    }

    [Fact]
    public void AllMappedKinds_HaveConceptInCatalog()
    {
        var catalog = AutoDerivedFailureOntologyCatalog.Instance;
        foreach (var kind in Enum.GetValues<RunHealthSignalKind>())
        {
            catalog.FromSignalKind(kind)?.Name.Should().NotBeNullOrEmpty(
                $"every RunHealthSignalKind must have a concept name; missing: {kind}");
        }
    }
}
