// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Prometheus;
using Vais.Agents.Observability.Prometheus;
using Xunit;

namespace Vais.Agents.Observability.Tests;

public sealed class PrometheusSectionSinkTests
{
    private static (PrometheusSectionSink sink, MetricFactory factory) Build()
    {
        // Isolated registry per test — no global state pollution.
        var registry = Metrics.NewCustomRegistry();
        var factory = Metrics.WithCustomRegistry(registry);
        return (new PrometheusSectionSink(factory), factory);
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

    private static SectionTelemetrySnapshot Snap(
        string agentId,
        double usedRatio,
        params SectionMeasurement[] sections)
        => new(
            Context: new AgentContext { RunId = "run-1", AgentName = agentId },
            TurnIndex: 1,
            Sections: sections,
            Budget: new SectionBudgetSummary(null, null, sections.Sum(s => s.Chars), null, usedRatio, 0, 0));

    private static Counter Counter(MetricFactory factory, string name, string[] labels)
        => factory.CreateCounter(name, "", new CounterConfiguration { LabelNames = labels });

    private static Histogram Histogram(MetricFactory factory, string name, string[] labels)
        => factory.CreateHistogram(name, "", new HistogramConfiguration { LabelNames = labels });

    [Fact]
    public async Task Sections_Per_Turn_And_Budget_Used_Ratio_Observed_Once()
    {
        var (sink, factory) = Build();

        await sink.EmitAsync(Snap("agent-a", usedRatio: 0.42,
            M("system.persona", chars: 10),
            M("retrieval.docs", chars: 50)));

        var sectionsPerTurn = Histogram(factory, "vais_request_sections_per_turn", ["agent_id"]);
        var sample = sectionsPerTurn.WithLabels("agent-a");
        sample.Count.Should().Be(1);
        sample.Sum.Should().Be(2);

        var budgetRatio = Histogram(factory, "vais_request_budget_used_ratio", ["agent_id"]);
        budgetRatio.WithLabels("agent-a").Sum.Should().BeApproximately(0.42, 0.001);
    }

    [Fact]
    public async Task Section_Chars_Histogram_Records_With_Full_Label_Set()
    {
        var (sink, factory) = Build();

        await sink.EmitAsync(Snap("agent-a", 0.0,
            M("retrieval.docs", producer: "rag", chars: 412)));

        var hist = Histogram(factory, "vais_request_section_chars",
            ["section_id", "kind", "producer", "agent_id"]);
        hist.WithLabels("retrieval.docs", "SystemSegment", "rag", "agent-a").Sum.Should().Be(412);
    }

    [Fact]
    public async Task Section_Tokens_Histogram_Only_Recorded_When_Counter_Available()
    {
        var (sink, factory) = Build();

        await sink.EmitAsync(Snap("agent-a", 0.0,
            M("a", chars: 100, tokens: 25),
            M("b", chars: 100, tokens: null)));

        var hist = Histogram(factory, "vais_request_section_tokens",
            ["section_id", "kind", "producer", "agent_id"]);
        hist.WithLabels("a", "SystemSegment", "_unknown", "agent-a").Sum.Should().Be(25);
        hist.WithLabels("b", "SystemSegment", "_unknown", "agent-a").Count.Should().Be(0);
    }

    [Fact]
    public async Task Section_Ratio_Histogram_Records_With_Two_Labels_Only()
    {
        var (sink, factory) = Build();

        await sink.EmitAsync(Snap("agent-a", 0.0,
            M("a", chars: 30, ratio: 0.3)));

        var hist = Histogram(factory, "vais_request_section_ratio", ["section_id", "agent_id"]);
        hist.WithLabels("a", "agent-a").Sum.Should().BeApproximately(0.3, 0.001);
    }

    [Fact]
    public async Task Outcome_Counter_Increments_Per_Section_With_Outcome_Label()
    {
        var (sink, factory) = Build();

        await sink.EmitAsync(Snap("agent-a", 0.0,
            M("a", outcome: "included"),
            M("b", outcome: "dropped"),
            M("c", outcome: "included")));

        var counter = Counter(factory, "vais_request_section_outcome_total", ["section_id", "outcome"]);
        counter.WithLabels("a", "included").Value.Should().Be(1);
        counter.WithLabels("b", "dropped").Value.Should().Be(1);
        counter.WithLabels("c", "included").Value.Should().Be(1);
    }

    [Fact]
    public async Task Null_AgentId_Falls_Back_To_Unknown_Label()
    {
        var (sink, factory) = Build();

        await sink.EmitAsync(new SectionTelemetrySnapshot(
            Context: AgentContext.Empty,
            TurnIndex: 1,
            Sections: new[] { M("a", producer: "rag", chars: 10) },
            Budget: new SectionBudgetSummary(null, null, 10, null, 0, 0, 0)));

        var hist = Histogram(factory, "vais_request_section_chars",
            ["section_id", "kind", "producer", "agent_id"]);
        hist.WithLabels("a", "SystemSegment", "rag", "_unknown").Count.Should().Be(1);
    }

    [Fact]
    public async Task Null_ProducerId_Falls_Back_To_Unknown_Label()
    {
        var (sink, factory) = Build();

        await sink.EmitAsync(Snap("agent-a", 0.0, M("a", producer: null, chars: 5)));

        var hist = Histogram(factory, "vais_request_section_chars",
            ["section_id", "kind", "producer", "agent_id"]);
        hist.WithLabels("a", "SystemSegment", "_unknown", "agent-a").Count.Should().Be(1);
    }

    [Fact]
    public async Task Multiple_Turns_Accumulate_Counts()
    {
        var (sink, factory) = Build();

        await sink.EmitAsync(Snap("agent-a", 0.0, M("a", chars: 10)));
        await sink.EmitAsync(Snap("agent-a", 0.0, M("a", chars: 20)));
        await sink.EmitAsync(Snap("agent-a", 0.0, M("a", chars: 30)));

        var hist = Histogram(factory, "vais_request_section_chars",
            ["section_id", "kind", "producer", "agent_id"]);
        var sample = hist.WithLabels("a", "SystemSegment", "_unknown", "agent-a");
        sample.Count.Should().Be(3);
        sample.Sum.Should().Be(60);
    }
}
