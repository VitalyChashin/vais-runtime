// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Vais.Agents.Core;

namespace Vais.Agents.Observability.OpenTelemetry;

/// <summary>
/// <see cref="IUsageSink"/> implementation that writes each <see cref="UsageRecord"/> onto
/// an OpenTelemetry <see cref="Meter"/> using the GenAI semantic conventions (see ADR 0002).
/// </summary>
/// <remarks>
/// Emits two instruments:
/// <list type="bullet">
///   <item><c>gen_ai.client.token.usage</c> (histogram, unit <c>{token}</c>) — one datapoint per token-type.</item>
///   <item><c>gen_ai.client.operation.duration</c> (histogram, unit <c>s</c>) — per-turn wall-clock time.</item>
/// </list>
/// The meter is published under <see cref="AgenticDiagnostics.MeterName"/>, so consumers pick it up with
/// <c>.AddMeter("Vais.Agents")</c> on their <c>MeterProviderBuilder</c> (or use
/// <see cref="AgenticOpenTelemetryExtensions.AddAgenticInstrumentation(global::OpenTelemetry.Metrics.MeterProviderBuilder)"/>).
/// </remarks>
public sealed class OpenTelemetryUsageSink : IUsageSink, IDisposable
{
    private readonly Meter _meter;
    private readonly Histogram<long> _tokenUsage;
    private readonly Histogram<double> _operationDuration;

    /// <summary>
    /// Create a sink that publishes metrics under <see cref="AgenticDiagnostics.MeterName"/>.
    /// </summary>
    public OpenTelemetryUsageSink()
    {
        _meter = new Meter(AgenticDiagnostics.MeterName);
        _tokenUsage = _meter.CreateHistogram<long>(
            AgenticMetrics.TokenUsage,
            unit: "{token}",
            description: "Measures number of input and output tokens used.");
        _operationDuration = _meter.CreateHistogram<double>(
            AgenticMetrics.OperationDuration,
            unit: "s",
            description: "Measures the duration of GenAI operations in seconds.");
    }

    /// <inheritdoc />
    public ValueTask ReportAsync(UsageRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        // Tags common to every instrument. Kept as a small TagList so the hot path
        // avoids allocating a dictionary per call.
        var tags = BuildCommonTags(record);

        if (record.PromptTokens is int prompt)
        {
            var tokenTags = tags;
            tokenTags.Add(AgenticTags.GenAiTokenType, "input");
            _tokenUsage.Record(prompt, tokenTags);
        }
        if (record.CompletionTokens is int completion)
        {
            var tokenTags = tags;
            tokenTags.Add(AgenticTags.GenAiTokenType, "output");
            _tokenUsage.Record(completion, tokenTags);
        }

        var durationTags = tags;
        if (!record.Succeeded && record.ErrorType is not null)
        {
            durationTags.Add(AgenticTags.ErrorType, record.ErrorType);
        }
        _operationDuration.Record(record.Duration.TotalSeconds, durationTags);

        return default;
    }

    private static TagList BuildCommonTags(UsageRecord record)
    {
        var tags = new TagList
        {
            { AgenticTags.GenAiSystem, record.ProviderName },
            { AgenticTags.GenAiResponseModel, record.ModelId },
            { AgenticTags.GenAiOperationName, "chat" },
        };
        return tags;
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
