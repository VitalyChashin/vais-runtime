// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Prometheus;
using Vais.Agents.Eval.Continuous;

namespace Vais.Agents.Observability.Prometheus;

/// <summary>
/// <see cref="IContinuousScoringMetricSink"/> that emits four Prometheus instruments for
/// continuous eval scoring. Metrics are written to the default Prometheus registry.
/// </summary>
/// <remarks>
/// Instruments:
/// <list type="bullet">
///   <item><c>vais_eval_continuous_cases_total{suite_id,status}</c> — counter incremented per scored sample.</item>
///   <item><c>vais_eval_continuous_assertion_score{suite_id,kind}</c> — histogram per scored assertion (1.0 = Pass, 0.0 = Fail when no numeric score).</item>
///   <item><c>vais_eval_continuous_window_sampled{suite_id}</c> — gauge: current window's sampled count.</item>
///   <item><c>vais_eval_continuous_window_seconds_remaining{suite_id}</c> — gauge: seconds until current window closes.</item>
/// </list>
/// </remarks>
public sealed class PrometheusContinuousScoringMetricSink : IContinuousScoringMetricSink
{
    private static readonly double[] ScoreBuckets = [0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0];

    private readonly Counter _casesTotal;
    private readonly Histogram _assertionScore;
    private readonly Gauge _windowSampled;
    private readonly Gauge _windowSecondsRemaining;

    /// <summary>Initialise against the default Prometheus registry. Intended for DI use.</summary>
    public PrometheusContinuousScoringMetricSink()
        : this(Metrics.WithCustomRegistry(Metrics.DefaultRegistry))
    {
    }

    /// <summary>Initialise against a specific <see cref="MetricFactory"/>. Use in tests with an isolated registry.</summary>
    public PrometheusContinuousScoringMetricSink(MetricFactory metricFactory)
    {
        ArgumentNullException.ThrowIfNull(metricFactory);

        _casesTotal = metricFactory.CreateCounter(
            "vais_eval_continuous_cases_total",
            "Number of continuous eval cases scored, by suite and status (Pass/Fail).",
            new CounterConfiguration { LabelNames = ["suite_id", "status"] });

        _assertionScore = metricFactory.CreateHistogram(
            "vais_eval_continuous_assertion_score",
            "Score observed per scored assertion. 1.0 = Pass, 0.0 = Fail when no numeric score provided.",
            new HistogramConfiguration
            {
                LabelNames = ["suite_id", "kind"],
                Buckets = ScoreBuckets,
            });

        _windowSampled = metricFactory.CreateGauge(
            "vais_eval_continuous_window_sampled",
            "Number of production runs sampled in the current eval window.",
            new GaugeConfiguration { LabelNames = ["suite_id"] });

        _windowSecondsRemaining = metricFactory.CreateGauge(
            "vais_eval_continuous_window_seconds_remaining",
            "Seconds remaining until the current eval window closes and a new one opens.",
            new GaugeConfiguration { LabelNames = ["suite_id"] });
    }

    /// <inheritdoc/>
    public void RecordSample(string suiteId, string status) =>
        _casesTotal.WithLabels(suiteId, status).Inc();

    /// <inheritdoc/>
    public void ObserveAssertionScore(string suiteId, string kind, double score) =>
        _assertionScore.WithLabels(suiteId, kind).Observe(score);

    /// <inheritdoc/>
    public void SetWindowSampledCount(string suiteId, double count) =>
        _windowSampled.WithLabels(suiteId).Set(count);

    /// <inheritdoc/>
    public void SetWindowSecondsRemaining(string suiteId, double seconds) =>
        _windowSecondsRemaining.WithLabels(suiteId).Set(seconds);
}
