// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Vais.Agents.Runtime.Plugins.Container.Otlp;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Container.Tests.Otlp;

public sealed class OtlpSpanForwarderTests : IDisposable
{
    private readonly List<Activity> _emitted = [];
    private readonly ActivityListener _listener;

    public OtlpSpanForwarderTests()
    {
        _listener = new ActivityListener
        {
            // Use the string literal to avoid a circular init: if we reference OtlpSpanForwarder.Source
            // here, the ActivitySource.ctor fires the listener before the static field is set.
            ShouldListenTo = s => s.Name == "Vais.Agents.Runtime.Plugins.Container.Otlp",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => _emitted.Add(a),
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    private static OtlpSpan MakeSpan(
        string name = "test-span",
        ulong startNano = 1_000_000_000UL,
        ulong endNano   = 2_000_000_000UL)
    {
        var traceIdBytes = new byte[16];
        traceIdBytes[0] = 0xAA;
        var spanIdBytes = new byte[8];
        spanIdBytes[0] = 0xBB;

        return new OtlpSpan
        {
            TraceId = ByteString.CopyFrom(traceIdBytes),
            SpanId  = ByteString.CopyFrom(spanIdBytes),
            Name = name,
            Kind = 1,
            StartTimeUnixNano = startNano,
            EndTimeUnixNano   = endNano,
        };
    }

    [Fact]
    public void EmitSpan_ValidSpan_EmitsActivity()
    {
        var forwarder = new OtlpSpanForwarder(NullLogger.Instance);
        forwarder.EmitSpan(MakeSpan("my-op"), "my-agent");
        _emitted.Should().HaveCount(1);
        _emitted[0].OperationName.Should().Be("my-op");
    }

    [Fact]
    public void EmitSpan_SetsAgentIdTag()
    {
        var forwarder = new OtlpSpanForwarder(NullLogger.Instance);
        forwarder.EmitSpan(MakeSpan(), "agent-42");
        _emitted[0].GetTagItem("vais.agent_id").Should().Be("agent-42");
    }

    [Fact]
    public void EmitSpan_SetsSourceTag()
    {
        var forwarder = new OtlpSpanForwarder(NullLogger.Instance);
        forwarder.EmitSpan(MakeSpan(), "agent-1");
        _emitted[0].GetTagItem("vais.span.source").Should().Be("plugin_otlp");
    }

    [Fact]
    public void EmitSpan_OverridesStartTime()
    {
        var forwarder = new OtlpSpanForwarder(NullLogger.Instance);
        // 1 000 000 000 ns = 1 second after epoch = 1970-01-01T00:00:01Z
        forwarder.EmitSpan(MakeSpan(startNano: 1_000_000_000UL, endNano: 2_000_000_000UL), "a");
        var start = _emitted[0].StartTimeUtc;
        start.Should().BeCloseTo(new DateTime(1970, 1, 1, 0, 0, 1, DateTimeKind.Utc), TimeSpan.FromMilliseconds(2));
    }

    [Fact]
    public void EmitSpan_InvalidTraceId_DoesNotEmit()
    {
        var forwarder = new OtlpSpanForwarder(NullLogger.Instance);
        var span = new OtlpSpan
        {
            TraceId = ByteString.CopyFrom(new byte[8]), // wrong length
            SpanId  = ByteString.CopyFrom(new byte[8]),
            Name = "bad",
        };
        forwarder.EmitSpan(span, "a");
        _emitted.Should().BeEmpty();
    }

    [Fact]
    public void Forward_EmitsAllSpans()
    {
        var forwarder = new OtlpSpanForwarder(NullLogger.Instance);
        var spans = new[] { MakeSpan("op1"), MakeSpan("op2"), MakeSpan("op3") };
        forwarder.Forward(spans, "agent-x");
        _emitted.Should().HaveCount(3);
    }
}
