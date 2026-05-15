// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Prometheus;

namespace Vais.Agents.Observability.Prometheus;

/// <summary>
/// <see cref="ISectionTelemetrySink"/> that emits per-turn Prometheus metrics for the section
/// pipeline. Six instruments cover per-section size + outcome attribution and per-turn
/// aggregates suitable for Grafana panels and operator alerts.
/// </summary>
/// <remarks>
/// <para>Metrics (all prefixed <c>vais_request_*</c>):</para>
/// <list type="bullet">
///   <item><description><b><c>vais_request_section_chars</c></b> (histogram, labels: <c>section_id, kind, producer, agent_id</c>) — per-section char count.</description></item>
///   <item><description><b><c>vais_request_section_tokens</c></b> (histogram, same labels) — per-section token count, emitted only when an <see cref="ITokenCounter"/> was configured.</description></item>
///   <item><description><b><c>vais_request_section_ratio</c></b> (histogram, labels: <c>section_id, agent_id</c>) — section share of the turn (0–1).</description></item>
///   <item><description><b><c>vais_request_section_outcome_total</c></b> (counter, labels: <c>section_id, outcome</c>) — count of <c>included</c> / <c>truncated</c> / <c>dropped</c> / <c>legacy</c> outcomes.</description></item>
///   <item><description><b><c>vais_request_budget_used_ratio</c></b> (histogram, labels: <c>agent_id</c>) — per-turn budget pressure (used / target, 0–1).</description></item>
///   <item><description><b><c>vais_request_sections_per_turn</c></b> (histogram, labels: <c>agent_id</c>) — count of sections in the turn.</description></item>
/// </list>
/// <para>
/// <b>Cardinality.</b> <c>section_id</c> is bounded by the registered producer chain (typically
/// 5–10 ids); <c>kind</c> is one of 7 enum values; <c>outcome</c> is one of 4; <c>producer</c>
/// is bounded by the registered producer types; <c>agent_id</c> is per-agent (operator concern).
/// Combined cardinality is dominated by <c>agent_id × section_id</c>; size accordingly.
/// </para>
/// <para>
/// Metrics are written via <c>prometheus-net</c> to the registry implied by the supplied
/// <see cref="MetricFactory"/>. The DI helper (<c>AddAgenticPrometheusSectionSink</c>) writes
/// to the default registry; tests pass an isolated factory to avoid global state pollution.
/// </para>
/// </remarks>
public sealed class PrometheusSectionSink : ISectionTelemetrySink
{
    private readonly Histogram _sectionChars;
    private readonly Histogram _sectionTokens;
    private readonly Histogram _sectionRatio;
    private readonly Counter _sectionOutcomeTotal;
    private readonly Histogram _budgetUsedRatio;
    private readonly Histogram _sectionsPerTurn;

    // Bucket arrays tuned for context-window observability. char/token buckets cover the
    // 0–32k range typical of modern LLM context windows; ratio buckets cover 0–1.
    private static readonly double[] SizeBuckets = [0, 16, 64, 256, 1024, 4096, 16_384, 65_536];
    private static readonly double[] RatioBuckets = [0.01, 0.05, 0.1, 0.25, 0.5, 0.75, 0.9, 1.0];
    private static readonly double[] CountBuckets = [1, 2, 5, 10, 20, 50];

    /// <summary>Initialise the sink against the default Prometheus registry. Intended for DI use.</summary>
    public PrometheusSectionSink()
        : this(Metrics.WithCustomRegistry(Metrics.DefaultRegistry))
    {
    }

    /// <summary>Initialise the sink against a specific <see cref="MetricFactory"/>. Use in tests with an isolated registry to avoid global pollution.</summary>
    public PrometheusSectionSink(MetricFactory metricFactory)
    {
        ArgumentNullException.ThrowIfNull(metricFactory);

        _sectionChars = metricFactory.CreateHistogram(
            "vais_request_section_chars",
            "Character count of a section as it enters the section pipeline.",
            new HistogramConfiguration
            {
                LabelNames = ["section_id", "kind", "producer", "agent_id"],
                Buckets = SizeBuckets,
            });

        _sectionTokens = metricFactory.CreateHistogram(
            "vais_request_section_tokens",
            "Token count of a section (only emitted when an ITokenCounter is configured on the budget).",
            new HistogramConfiguration
            {
                LabelNames = ["section_id", "kind", "producer", "agent_id"],
                Buckets = SizeBuckets,
            });

        _sectionRatio = metricFactory.CreateHistogram(
            "vais_request_section_ratio",
            "Section share of the turn's total chars, 0-1.",
            new HistogramConfiguration
            {
                LabelNames = ["section_id", "agent_id"],
                Buckets = RatioBuckets,
            });

        _sectionOutcomeTotal = metricFactory.CreateCounter(
            "vais_request_section_outcome_total",
            "Count of per-section outcomes: included / truncated / dropped / legacy.",
            new CounterConfiguration
            {
                LabelNames = ["section_id", "outcome"],
            });

        _budgetUsedRatio = metricFactory.CreateHistogram(
            "vais_request_budget_used_ratio",
            "Per-turn budget pressure (used / target, 0-1).",
            new HistogramConfiguration
            {
                LabelNames = ["agent_id"],
                Buckets = RatioBuckets,
            });

        _sectionsPerTurn = metricFactory.CreateHistogram(
            "vais_request_sections_per_turn",
            "Number of sections in the turn (one observation per turn).",
            new HistogramConfiguration
            {
                LabelNames = ["agent_id"],
                Buckets = CountBuckets,
            });
    }

    /// <inheritdoc />
    public ValueTask EmitAsync(SectionTelemetrySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var agentId = snapshot.AgentId ?? "_unknown";

        _sectionsPerTurn.WithLabels(agentId).Observe(snapshot.Sections.Count);
        _budgetUsedRatio.WithLabels(agentId).Observe(snapshot.Budget.UsedRatio);

        foreach (var section in snapshot.Sections)
        {
            var producer = section.ProducerId ?? "_unknown";
            var kind = section.Kind.ToString();

            _sectionChars.WithLabels(section.Id, kind, producer, agentId).Observe(section.Chars);
            _sectionRatio.WithLabels(section.Id, agentId).Observe(section.Ratio);
            _sectionOutcomeTotal.WithLabels(section.Id, section.Outcome).Inc();

            if (section.Tokens is int tokens)
            {
                _sectionTokens.WithLabels(section.Id, kind, producer, agentId).Observe(tokens);
            }
        }

        return ValueTask.CompletedTask;
    }
}
