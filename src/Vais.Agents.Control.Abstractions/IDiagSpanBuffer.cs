// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// In-process span buffer populated by <c>DiagSpanBuffer</c> when
/// <c>VAIS_DIAG_SPAN_BUFFER=true</c>. Exposed by <c>GET /v1/diagnostics/spans</c>
/// and consumed by <c>vais diagnose spans</c> / <c>vais diagnose trace</c>.
/// Off by default — dev-only, lossy, single-silo.
/// </summary>
public interface IDiagSpanBuffer
{
    /// <summary><c>true</c> when the buffer exporter is registered and receiving spans.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Return up to <paramref name="limit"/> recent spans in descending start-time order.
    /// When <paramref name="source"/> is non-null, only spans whose <see cref="DiagSpanRecord.Source"/>
    /// matches (case-insensitive) are returned.
    /// </summary>
    IReadOnlyList<DiagSpanRecord> GetSpans(string? source, int limit);
}

/// <summary>A single captured span from <see cref="IDiagSpanBuffer"/>.</summary>
/// <param name="TraceId">W3C trace-id hex string (32 chars).</param>
/// <param name="SpanId">W3C span-id hex string (16 chars).</param>
/// <param name="ParentSpanId">Parent span-id, or <see langword="null"/> for root spans.</param>
/// <param name="Name">Span operation name.</param>
/// <param name="Source"><see cref="System.Diagnostics.ActivitySource"/> name that created this span.</param>
/// <param name="StartTime">UTC wall-clock start of the span.</param>
/// <param name="DurationMs">Duration in milliseconds.</param>
/// <param name="Status"><c>"Ok"</c>, <c>"Error"</c>, or <c>"Unset"</c>.</param>
/// <param name="Attributes">Span tag key/value pairs.</param>
public sealed record DiagSpanRecord(
    string TraceId,
    string SpanId,
    string? ParentSpanId,
    string Name,
    string Source,
    DateTimeOffset StartTime,
    long DurationMs,
    string Status,
    IReadOnlyDictionary<string, string> Attributes);
