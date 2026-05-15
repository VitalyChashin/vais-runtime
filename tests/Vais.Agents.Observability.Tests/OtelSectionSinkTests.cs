// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using FluentAssertions;
using Vais.Agents.Observability.OpenTelemetry;
using Xunit;

namespace Vais.Agents.Observability.Tests;

public sealed class OtelSectionSinkTests
{
    private const string SourceName = "Vais.Agents.Observability.Tests.OtelSectionSink";

    private static readonly ActivitySource Source = new(SourceName);

    private static ActivityListener AttachListener()
    {
        // Lambda captures the const SourceName, not a static field — important because
        // listeners are walked inside the ActivitySource constructor, and a captured field
        // reference can still be null during cctor.
        var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static SectionTelemetrySnapshot Snapshot(
        IReadOnlyList<SectionMeasurement> sections,
        SectionBudgetSummary budget,
        int turnIndex = 1,
        string? runId = null,
        string? agentId = null)
    {
        var ctx = runId is null && agentId is null
            ? AgentContext.Empty
            : new AgentContext { RunId = runId, AgentName = agentId };
        return new SectionTelemetrySnapshot(ctx, turnIndex, sections, budget);
    }

    private static SectionMeasurement M(
        string id,
        SectionKind kind = SectionKind.SystemSegment,
        string? producer = null,
        int? order = null,
        int? priority = null,
        int chars = 0,
        int? tokens = null,
        double ratio = 0,
        string outcome = "included",
        int droppedChars = 0)
        => new(id, kind, producer, order, priority, chars, tokens, ratio, outcome, droppedChars);

    private static SectionBudgetSummary B(
        int? targetChars = null,
        int? targetTokens = null,
        int usedChars = 0,
        int? usedTokens = null,
        double usedRatio = 0,
        int droppedCount = 0,
        int truncatedCount = 0)
        => new(targetChars, targetTokens, usedChars, usedTokens, usedRatio, droppedCount, truncatedCount);

    [Fact]
    public async Task NoOp_When_No_Activity_Current()
    {
        // Nothing started → Activity.Current is null. The sink should not throw.
        Activity.Current.Should().BeNull();

        await OtelSectionSink.Instance.EmitAsync(
            Snapshot(
                new[] { M("system.persona", chars: 10, ratio: 1.0) },
                B(usedChars: 10)));
        // No assertion needed — the test passes if nothing throws.
    }

    [Fact]
    public async Task Sets_Aggregate_Tags_On_Current_Activity()
    {
        using var listener = AttachListener();
        using var activity = Source.StartActivity("test-turn", ActivityKind.Client);
        activity.Should().NotBeNull();

        await OtelSectionSink.Instance.EmitAsync(
            Snapshot(
                new[]
                {
                    M("system.persona", chars: 32, ratio: 0.4),
                    M("retrieval.docs", chars: 48, ratio: 0.6),
                },
                B(targetChars: 4096, usedChars: 80, usedRatio: 0.0195, droppedCount: 0, truncatedCount: 0),
                turnIndex: 3));

        var tags = activity!.TagObjects.ToDictionary(kv => kv.Key, kv => kv.Value);
        tags["vais.request.turn_index"].Should().Be(3);
        tags["vais.request.section_count"].Should().Be(2);
        tags["vais.request.total_chars"].Should().Be(80);
        tags["vais.request.budget.target_chars"].Should().Be(4096);
        tags["vais.request.budget_used_ratio"].Should().Be(0.0195);
        tags["vais.request.budget.dropped_count"].Should().Be(0);
        tags["vais.request.budget.truncated_count"].Should().Be(0);
    }

    [Fact]
    public async Task Token_Aggregates_Emit_Only_When_Counter_Was_Configured()
    {
        using var listener = AttachListener();
        using var activity = Source.StartActivity("test-turn");

        await OtelSectionSink.Instance.EmitAsync(
            Snapshot(
                new[] { M("a", chars: 100, tokens: 25, ratio: 1.0) },
                B(targetTokens: 200, usedChars: 100, usedTokens: 25, usedRatio: 0.125)));

        var tags = activity!.TagObjects.ToDictionary(kv => kv.Key, kv => kv.Value);
        tags["vais.request.total_tokens_est"].Should().Be(25);
        tags["vais.request.budget.target_tokens"].Should().Be(200);
        tags.Should().NotContainKey("vais.request.budget.target_chars");
    }

    [Fact]
    public async Task Per_Section_Tags_Use_Section_Id_In_Tag_Name()
    {
        using var listener = AttachListener();
        using var activity = Source.StartActivity("test-turn");

        await OtelSectionSink.Instance.EmitAsync(
            Snapshot(
                new[]
                {
                    M("system.persona",
                      kind: SectionKind.SystemSegment,
                      producer: "PersonaContributor",
                      order: 0,
                      priority: 0,
                      chars: 32,
                      ratio: 0.4,
                      outcome: "included"),
                    M("retrieval.docs",
                      kind: SectionKind.SystemSegment,
                      producer: "KnowledgeRetrievalContextProvider",
                      priority: 5,
                      chars: 48,
                      tokens: 12,
                      ratio: 0.6,
                      outcome: "included"),
                },
                B(usedChars: 80)));

        var tags = activity!.TagObjects.ToDictionary(kv => kv.Key, kv => kv.Value);

        // Section 1 — system.persona
        tags["vais.request.section.system.persona.kind"].Should().Be("SystemSegment");
        tags["vais.request.section.system.persona.chars"].Should().Be(32);
        tags["vais.request.section.system.persona.ratio"].Should().Be("0.4");
        tags["vais.request.section.system.persona.outcome"].Should().Be("included");
        tags["vais.request.section.system.persona.producer"].Should().Be("PersonaContributor");
        tags["vais.request.section.system.persona.order"].Should().Be(0);
        tags.Should().NotContainKey("vais.request.section.system.persona.tokens");
        tags.Should().NotContainKey("vais.request.section.system.persona.dropped_chars");

        // Section 2 — retrieval.docs
        tags["vais.request.section.retrieval.docs.chars"].Should().Be(48);
        tags["vais.request.section.retrieval.docs.tokens"].Should().Be(12);
        tags.Should().NotContainKey("vais.request.section.retrieval.docs.order");
    }

    [Fact]
    public async Task Dropped_Sections_Emit_Outcome_And_Dropped_Chars_Tag()
    {
        using var listener = AttachListener();
        using var activity = Source.StartActivity("test-turn");

        await OtelSectionSink.Instance.EmitAsync(
            Snapshot(
                new[]
                {
                    M("retrieval.docs", outcome: "dropped", droppedChars: 412),
                },
                B(usedChars: 0, droppedCount: 1)));

        var tags = activity!.TagObjects.ToDictionary(kv => kv.Key, kv => kv.Value);
        tags["vais.request.section.retrieval.docs.outcome"].Should().Be("dropped");
        tags["vais.request.section.retrieval.docs.dropped_chars"].Should().Be(412);
        tags["vais.request.budget.dropped_count"].Should().Be(1);
    }

    [Fact]
    public async Task Section_Without_Producer_Or_Order_Or_Tokens_Omits_Those_Tags()
    {
        using var listener = AttachListener();
        using var activity = Source.StartActivity("test-turn");

        await OtelSectionSink.Instance.EmitAsync(
            Snapshot(
                new[] { M("history.user.0", kind: SectionKind.UserMessage, chars: 5, ratio: 1.0) },
                B(usedChars: 5)));

        var tags = activity!.TagObjects.ToDictionary(kv => kv.Key, kv => kv.Value);
        tags["vais.request.section.history.user.0.kind"].Should().Be("UserMessage");
        tags["vais.request.section.history.user.0.chars"].Should().Be(5);
        tags.Should().NotContainKey("vais.request.section.history.user.0.producer");
        tags.Should().NotContainKey("vais.request.section.history.user.0.order");
        tags.Should().NotContainKey("vais.request.section.history.user.0.tokens");
        tags.Should().NotContainKey("vais.request.section.history.user.0.dropped_chars");
    }
}
