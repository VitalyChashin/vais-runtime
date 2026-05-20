// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Runtime.Extensions.Tests.Observability;

/// <summary>
/// Unit tests for <see cref="InMemoryExtensionMetricsService"/>.
/// Each test uses a unique extension id to avoid cross-test MeterListener contamination.
/// </summary>
public sealed class InMemoryExtensionMetricsServiceTests : IDisposable
{
    private readonly InMemoryExtensionMetricsService _svc = new();
    private readonly Meter _meter = new(ExtensionTelemetry.MeterName);

    public void Dispose()
    {
        _svc.Dispose();
        _meter.Dispose();
    }

    // ── 1. No samples → GetMetrics returns null ───────────────────────────────

    [Fact]
    public void GetMetrics_NoSamples_ReturnsNull()
    {
        _svc.GetMetrics("ext-unknown").Should().BeNull();
    }

    // ── 2. Single sample → p50 == p95 == that value ──────────────────────────

    [Fact]
    public void GetMetrics_OneSample_PercentilesEqualSingleValue()
    {
        var histogram = _meter.CreateHistogram<double>("vais_extension_handler_invoke_duration_seconds");
        histogram.Record(0.5,
            new TagList
            {
                { "extension", "ext-p1" },
                { "handler",   "h1" },
                { "seam",      "agentInput" },
                { "action",    "next" },
            });

        var result = _svc.GetMetrics("ext-p1");

        result.Should().NotBeNull();
        result!.Handlers.Should().HaveCount(1);
        var h = result.Handlers[0];
        h.P50Seconds.Should().BeApproximately(0.5, 1e-9);
        h.P95Seconds.Should().BeApproximately(0.5, 1e-9);
        h.ErrorRate.Should().Be(0.0);
        h.TotalInvocations.Should().Be(1);
    }

    // ── 3. Multiple samples → p50/p95 computed correctly ─────────────────────

    [Fact]
    public void GetMetrics_ManySamples_PercentilesCorrect()
    {
        var histogram = _meter.CreateHistogram<double>("vais_extension_handler_invoke_duration_seconds");
        for (var i = 1; i <= 10; i++)
        {
            histogram.Record((double)i / 10,
                new TagList
                {
                    { "extension", "ext-p2" },
                    { "handler",   "h2" },
                    { "seam",      "agentInput" },
                    { "action",    "next" },
                });
        }

        var result = _svc.GetMetrics("ext-p2")!;
        var h = result.Handlers[0];

        // sorted values: 0.1, 0.2, ..., 1.0
        // p50 = index ceil(0.50 * 10) - 1 = 4 → 0.5
        // p95 = index ceil(0.95 * 10) - 1 = 9 → 1.0
        h.P50Seconds.Should().BeApproximately(0.5, 1e-9);
        h.P95Seconds.Should().BeApproximately(1.0, 1e-9);
        h.TotalInvocations.Should().Be(10);
    }

    // ── 4. Error rate: fail + skip count as errors ────────────────────────────

    [Fact]
    public void GetMetrics_FailAndSkip_CountAsErrors()
    {
        var histogram = _meter.CreateHistogram<double>("vais_extension_handler_invoke_duration_seconds");

        void Record(string action) =>
            histogram.Record(0.1, new TagList
            {
                { "extension", "ext-p3" },
                { "handler",   "h3" },
                { "seam",      "agentInput" },
                { "action",    action },
            });

        Record("next");
        Record("next");
        Record("fail");
        Record("skip");

        var h = _svc.GetMetrics("ext-p3")!.Handlers[0];
        h.ErrorRate.Should().BeApproximately(0.5, 1e-9); // 2/4
        h.TotalInvocations.Should().Be(4);
    }

    // ── 5. Multiple (handler, seam) pairs are tracked independently ───────────

    [Fact]
    public void GetMetrics_MultipleHandlers_TrackedSeparately()
    {
        var histogram = _meter.CreateHistogram<double>("vais_extension_handler_invoke_duration_seconds");

        histogram.Record(0.2, new TagList
        {
            { "extension", "ext-p4" }, { "handler", "hA" }, { "seam", "agentInput" }, { "action", "next" },
        });
        histogram.Record(0.8, new TagList
        {
            { "extension", "ext-p4" }, { "handler", "hB" }, { "seam", "agentOutput" }, { "action", "mutate" },
        });

        var result = _svc.GetMetrics("ext-p4")!;
        result.Handlers.Should().HaveCount(2);
        result.Handlers.Select(h => h.HandlerId).Should().Contain(["hA", "hB"]);
    }

    // ── 6. Extension id lookup is case-insensitive ────────────────────────────

    [Fact]
    public void GetMetrics_CaseInsensitiveLookup()
    {
        var histogram = _meter.CreateHistogram<double>("vais_extension_handler_invoke_duration_seconds");
        histogram.Record(0.1, new TagList
        {
            { "extension", "EXT-P5" },
            { "handler",   "h5" },
            { "seam",      "agentInput" },
            { "action",    "next" },
        });

        _svc.GetMetrics("ext-p5").Should().NotBeNull("lookup must be case-insensitive");
        _svc.GetMetrics("EXT-P5").Should().NotBeNull();
    }

    // ── 7. Extensions not matching the query are ignored ─────────────────────

    [Fact]
    public void GetMetrics_OtherExtensions_NotIncluded()
    {
        var histogram = _meter.CreateHistogram<double>("vais_extension_handler_invoke_duration_seconds");
        histogram.Record(0.1, new TagList
        {
            { "extension", "ext-other" },
            { "handler",   "h6" },
            { "seam",      "agentInput" },
            { "action",    "next" },
        });

        // Querying a different extension should return null (no samples recorded for it)
        _svc.GetMetrics("ext-p6").Should().BeNull();
    }
}
