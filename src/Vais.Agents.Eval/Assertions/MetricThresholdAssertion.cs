// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Globalization;
using System.Text.Json;

namespace Vais.Agents.Eval.Assertions;

/// <summary>
/// Passes when the specified run metric falls within optional <c>min</c>/<c>max</c> bounds.
/// Config: <c>{ "metric": "duration", "max": 30000 }</c>.
/// Supported metrics: <c>duration</c> (ms), <c>promptTokens</c>, <c>completionTokens</c>,
/// <c>totalTokens</c>, <c>toolCalls.count</c>.
/// </summary>
internal sealed class MetricThresholdAssertion : IEvalAssertion
{
    private readonly string _metric;
    private readonly double? _min;
    private readonly double? _max;

    /// <summary>Construct with metric name and optional bounds.</summary>
    public MetricThresholdAssertion(string metric, double? min, double? max)
    {
        _metric = metric;
        _min = min;
        _max = max;
    }

    /// <inheritdoc/>
    public string Kind => "metric-threshold";

    /// <inheritdoc/>
    public ValueTask<EvalAssertionResult> EvaluateAsync(EvalCaseContext ctx, EvalRunRecord run, CancellationToken ct)
    {
        double? value = _metric switch
        {
            "duration"          => run.Duration.TotalMilliseconds,
            "promptTokens"      => run.PromptTokens,
            "completionTokens"  => run.CompletionTokens,
            "totalTokens"       => run.PromptTokens is not null && run.CompletionTokens is not null
                                    ? run.PromptTokens.Value + run.CompletionTokens.Value
                                    : (run.PromptTokens ?? run.CompletionTokens),
            "toolCalls.count"   => run.JournalEntries.OfType<ToolCallRecorded>().Count(),
            _                   => null,
        };

        if (value is null)
        {
            if (_metric is not ("promptTokens" or "completionTokens" or "totalTokens"))
                return ValueTask.FromResult(new EvalAssertionResult(
                    EvalAssertionStatus.Error,
                    Score: null,
                    Reason: $"Unknown metric '{_metric}'. Supported: duration, promptTokens, completionTokens, totalTokens, toolCalls.count"));

            return ValueTask.FromResult(new EvalAssertionResult(
                EvalAssertionStatus.Skipped,
                Score: null,
                Reason: $"Metric '{_metric}' not available for this run (provider did not report token counts)"));
        }

        string? failReason = null;
        if (_min is not null && value < _min)
            failReason = string.Format(CultureInfo.InvariantCulture, "{0}={1:F0} < min={2:F0}", _metric, value, _min);
        else if (_max is not null && value > _max)
            failReason = string.Format(CultureInfo.InvariantCulture, "{0}={1:F0} > max={2:F0}", _metric, value, _max);

        return ValueTask.FromResult(new EvalAssertionResult(
            failReason is null ? EvalAssertionStatus.Pass : EvalAssertionStatus.Fail,
            Score: failReason is null ? 1.0 : 0.0,
            Reason: failReason));
    }
}

/// <summary>Factory for <see cref="MetricThresholdAssertion"/>.</summary>
internal sealed class MetricThresholdAssertionFactory : IEvalAssertionFactory
{
    /// <inheritdoc/>
    public string Kind => "metric-threshold";

    /// <inheritdoc/>
    public IEvalAssertion Create(JsonElement args, IServiceProvider services)
    {
        var metric = "duration";
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("metric", out var mEl) && mEl.ValueKind == JsonValueKind.String)
            metric = mEl.GetString() ?? "duration";

        double? min = null, max = null;
        if (args.ValueKind == JsonValueKind.Object)
        {
            if (args.TryGetProperty("min", out var minEl) && minEl.ValueKind == JsonValueKind.Number)
                min = minEl.GetDouble();
            if (args.TryGetProperty("max", out var maxEl) && maxEl.ValueKind == JsonValueKind.Number)
                max = maxEl.GetDouble();
        }

        return new MetricThresholdAssertion(metric, min, max);
    }
}
