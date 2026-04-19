// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics.Metrics;
using FluentAssertions;
using Vais.Agents.Core;
using Vais.Agents.Observability.OpenTelemetry;
using Xunit;

namespace Vais.Agents.Observability.Tests;

/// <summary>
/// Verifies that <see cref="OpenTelemetryUsageSink"/> emits the expected GenAI
/// instruments with the correct dimensions. Uses <see cref="MeterListener"/> —
/// no OTel SDK required to observe the raw datapoints.
/// </summary>
public sealed class OpenTelemetryUsageSinkTests
{
    [Fact]
    public async Task Emits_Token_And_Duration_Measurements_On_Success()
    {
        var measurements = new List<Measurement>();
        using var listener = ListenToAgenticMeter(measurements);

        using var sink = new OpenTelemetryUsageSink();
        var record = new UsageRecord(
            ProviderName: "openai",
            ModelId: "gpt-4o",
            PromptTokens: 100,
            CompletionTokens: 50,
            Duration: TimeSpan.FromSeconds(1.5),
            StartedAt: DateTimeOffset.UtcNow,
            Succeeded: true);

        await sink.ReportAsync(record);

        measurements.Should().HaveCount(3); // input tokens, output tokens, duration

        var inputTokens = measurements.Single(m =>
            m.Name == AgenticMetrics.TokenUsage &&
            m.Tags.TryGetValue(AgenticTags.GenAiTokenType, out var t) &&
            (string?)t == "input");
        inputTokens.Value.Should().Be(100);
        inputTokens.Tags[AgenticTags.GenAiSystem].Should().Be("openai");
        inputTokens.Tags[AgenticTags.GenAiResponseModel].Should().Be("gpt-4o");

        var outputTokens = measurements.Single(m =>
            m.Name == AgenticMetrics.TokenUsage &&
            m.Tags.TryGetValue(AgenticTags.GenAiTokenType, out var t) &&
            (string?)t == "output");
        outputTokens.Value.Should().Be(50);

        var duration = measurements.Single(m => m.Name == AgenticMetrics.OperationDuration);
        duration.Value.Should().BeApproximately(1.5, 0.001);
        duration.Tags[AgenticTags.GenAiOperationName].Should().Be("chat");
        duration.Tags.Should().NotContainKey(AgenticTags.ErrorType);
    }

    [Fact]
    public async Task Duration_Measurement_Carries_ErrorType_On_Failure()
    {
        var measurements = new List<Measurement>();
        using var listener = ListenToAgenticMeter(measurements);

        using var sink = new OpenTelemetryUsageSink();
        var record = new UsageRecord(
            ProviderName: "openai",
            ModelId: "gpt-4o",
            PromptTokens: null,
            CompletionTokens: null,
            Duration: TimeSpan.FromMilliseconds(200),
            StartedAt: DateTimeOffset.UtcNow,
            Succeeded: false,
            ErrorType: "HttpRequestException");

        await sink.ReportAsync(record);

        var duration = measurements.Single(m => m.Name == AgenticMetrics.OperationDuration);
        duration.Tags[AgenticTags.ErrorType].Should().Be("HttpRequestException");
    }

    [Fact]
    public async Task Skips_Token_Instruments_When_Provider_Did_Not_Report_Usage()
    {
        var measurements = new List<Measurement>();
        using var listener = ListenToAgenticMeter(measurements);

        using var sink = new OpenTelemetryUsageSink();
        var record = new UsageRecord(
            ProviderName: "fake",
            ModelId: "x",
            PromptTokens: null,
            CompletionTokens: null,
            Duration: TimeSpan.FromMilliseconds(5),
            StartedAt: DateTimeOffset.UtcNow,
            Succeeded: true);

        await sink.ReportAsync(record);

        measurements.Should().ContainSingle()
            .Which.Name.Should().Be(AgenticMetrics.OperationDuration);
    }

    private static MeterListener ListenToAgenticMeter(List<Measurement> sink)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == AgenticDiagnostics.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
            sink.Add(new Measurement(instrument.Name, value, ToDict(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, state) =>
            sink.Add(new Measurement(instrument.Name, value, ToDict(tags))));
        listener.Start();
        return listener;
    }

    private static IReadOnlyDictionary<string, object?> ToDict(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dict = new Dictionary<string, object?>(tags.Length);
        foreach (var tag in tags)
        {
            dict[tag.Key] = tag.Value;
        }
        return dict;
    }

    internal sealed record Measurement(string Name, double Value, IReadOnlyDictionary<string, object?> Tags);
}
