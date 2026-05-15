// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Vais.Agents.Observability.Langfuse;
using Xunit;

namespace Vais.Agents.Observability.Tests;

public sealed class LangfuseSectionEnrichmentTests
{
    private const string SourceName = "Vais.Agents.Observability.Tests.LangfuseSection";

    private static readonly ActivitySource Source = new(SourceName);

    private static ActivityListener AttachListener()
    {
        // Lambda captures the const SourceName, not a static field — listeners are walked
        // inside the ActivitySource constructor, and a field reference can still be null
        // during the type's cctor (see OtelSectionSinkTests note).
        var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static SectionMeasurement M(
        string id,
        SectionKind kind = SectionKind.SystemSegment,
        string? producer = null,
        int chars = 0,
        int? tokens = null,
        double ratio = 0,
        string outcome = "included")
        => new(id, kind, producer, Order: null, Priority: null, chars, tokens, ratio, outcome, DroppedChars: 0);

    private static SectionTelemetrySnapshot Snapshot(params SectionMeasurement[] sections)
        => new(
            RunId: "run-1",
            AgentId: "agent-1",
            TurnIndex: 1,
            Sections: sections,
            Budget: new SectionBudgetSummary(null, null, sections.Sum(s => s.Chars), null, 0, 0, 0));

    [Fact]
    public async Task NoOp_When_No_Activity_Current()
    {
        Activity.Current.Should().BeNull();

        await LangfuseSectionEnrichment.Instance.EmitAsync(Snapshot(M("system.persona", chars: 5, ratio: 1.0)));
    }

    [Fact]
    public async Task Section_Id_Dots_Normalised_To_Underscores_In_Tag_Names()
    {
        using var listener = AttachListener();
        using var activity = Source.StartActivity("test-turn");

        await LangfuseSectionEnrichment.Instance.EmitAsync(Snapshot(
            M("retrieval.docs", producer: "rag", chars: 412, ratio: 0.89),
            M("memory.user.long", chars: 50, ratio: 0.11)));

        var tags = activity!.TagObjects.ToDictionary(kv => kv.Key, kv => kv.Value);

        // Outer key: dots → underscores. So `retrieval.docs` → `retrieval_docs`,
        // `memory.user.long` → `memory_user_long`.
        tags["langfuse.section.retrieval_docs.kind"].Should().Be("SystemSegment");
        tags["langfuse.section.retrieval_docs.producer"].Should().Be("rag");
        tags["langfuse.section.retrieval_docs.chars"].Should().Be(412);
        tags["langfuse.section.retrieval_docs.ratio"].Should().Be("0.89");

        tags["langfuse.section.memory_user_long.kind"].Should().Be("SystemSegment");
        tags["langfuse.section.memory_user_long.chars"].Should().Be(50);
    }

    [Fact]
    public async Task Producer_Tag_Omitted_When_ProducerId_Is_Null()
    {
        using var listener = AttachListener();
        using var activity = Source.StartActivity("test-turn");

        await LangfuseSectionEnrichment.Instance.EmitAsync(Snapshot(
            M("system.base", chars: 5, ratio: 1.0)));

        var tags = activity!.TagObjects.ToDictionary(kv => kv.Key, kv => kv.Value);
        tags.Should().NotContainKey("langfuse.section.system_base.producer");
        tags.Should().ContainKey("langfuse.section.system_base.kind");
    }

    [Fact]
    public async Task Tokens_Tag_Only_Emitted_When_Counter_Was_Configured()
    {
        using var listener = AttachListener();
        using var activity = Source.StartActivity("test-turn");

        await LangfuseSectionEnrichment.Instance.EmitAsync(Snapshot(
            M("a", chars: 100, tokens: 25, ratio: 0.5),
            M("b", chars: 100, tokens: null, ratio: 0.5)));

        var tags = activity!.TagObjects.ToDictionary(kv => kv.Key, kv => kv.Value);
        tags["langfuse.section.a.tokens"].Should().Be(25);
        tags.Should().NotContainKey("langfuse.section.b.tokens");
    }

    [Fact]
    public async Task Section_Breakdown_Blob_Has_Original_Ids_And_Rounded_Ratios()
    {
        using var listener = AttachListener();
        using var activity = Source.StartActivity("test-turn");

        await LangfuseSectionEnrichment.Instance.EmitAsync(Snapshot(
            M("system.persona", chars: 32, ratio: 0.069),
            M("retrieval.docs", chars: 412, ratio: 0.892)));

        var tags = activity!.TagObjects.ToDictionary(kv => kv.Key, kv => kv.Value);
        var json = tags["langfuse.trace.metadata.section_breakdown"]!.ToString()!;

        // The JSON preserves ORIGINAL section ids (dots intact) — it's a human-readable blob,
        // not a tag name, so no normalisation is applied.
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("system.persona").GetDouble().Should().BeApproximately(0.069, 0.0001);
        root.GetProperty("retrieval.docs").GetDouble().Should().BeApproximately(0.892, 0.0001);
    }

    [Fact]
    public async Task Id_Without_Dots_Passes_Through_Unchanged()
    {
        using var listener = AttachListener();
        using var activity = Source.StartActivity("test-turn");

        await LangfuseSectionEnrichment.Instance.EmitAsync(Snapshot(
            M("history", kind: SectionKind.UserMessage, chars: 5, ratio: 1.0)));

        var tags = activity!.TagObjects.ToDictionary(kv => kv.Key, kv => kv.Value);
        tags.Should().ContainKey("langfuse.section.history.kind");
    }
}
