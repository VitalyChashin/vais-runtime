// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Vais.Agents.Runtime.Plugins.Container.Otlp;

/// <summary>
/// Re-emits OTLP spans received from container plugins as .NET <see cref="Activity"/> objects
/// so they flow through the existing OpenTelemetry pipeline (exporters, Langfuse, etc.).
/// </summary>
internal sealed class OtlpSpanForwarder
{
    internal static readonly ActivitySource Source =
        new("Vais.Agents.Runtime.Plugins.Container.Otlp", "1.0.0");

    private readonly ILogger _logger;

    public OtlpSpanForwarder(ILogger logger) => _logger = logger;

    public void Forward(IReadOnlyList<OtlpSpan> spans, string agentId)
    {
        foreach (var span in spans)
            EmitSpan(span, agentId);
    }

    internal void EmitSpan(OtlpSpan span, string agentId)
    {
        if (span.TraceId.Length != 16 || span.SpanId.Length != 8)
        {
            _logger.LogDebug(
                "Dropping OTLP span '{Name}' from plugin '{AgentId}': invalid trace_id or span_id length",
                span.Name, agentId);
            return;
        }

        var traceId = ActivityTraceId.CreateFromBytes(span.TraceId.Span);
        var spanId  = ActivitySpanId.CreateFromBytes(span.SpanId.Span);

        ActivityContext parentContext;
        if (span.ParentSpanId.Length == 8)
        {
            var parentSpanId = ActivitySpanId.CreateFromBytes(span.ParentSpanId.Span);
            parentContext = new ActivityContext(traceId, parentSpanId, ActivityTraceFlags.Recorded, isRemote: true);
        }
        else
        {
            parentContext = new ActivityContext(traceId, spanId, ActivityTraceFlags.Recorded);
        }

        var kind = span.Kind switch
        {
            1 => ActivityKind.Internal,
            2 => ActivityKind.Server,
            3 => ActivityKind.Client,
            4 => ActivityKind.Producer,
            5 => ActivityKind.Consumer,
            _ => ActivityKind.Internal,
        };

        using var activity = Source.StartActivity(
            string.IsNullOrEmpty(span.Name) ? "(unnamed)" : span.Name,
            kind,
            parentContext);

        if (activity is null) return;

        // Override timing to match original plugin span.
        var startUtc = DateTimeOffset.FromUnixTimeMilliseconds(
            (long)(span.StartTimeUnixNano / 1_000_000)).UtcDateTime;
        var endUtc = DateTimeOffset.FromUnixTimeMilliseconds(
            (long)(span.EndTimeUnixNano / 1_000_000)).UtcDateTime;

        activity.SetStartTime(startUtc);

        activity.SetTag("vais.agent_id", agentId);
        activity.SetTag("vais.span.source", "plugin_otlp");

        foreach (var (key, value) in span.Attributes)
            activity.SetTag(key, value);

        // Must be called before Dispose so the OTel SDK picks up the correct end timestamp.
        activity.SetEndTime(endUtc);
    }
}
