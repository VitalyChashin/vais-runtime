// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Diagnostics;
using OpenTelemetry;
using Vais.Agents.Control;

namespace Vais.Agents.Runtime.Host.Diagnostics;

/// <summary>
/// Opt-in circular span buffer that implements both <see cref="BaseExporter{T}"/>
/// (OTel exporter) and <see cref="IDiagSpanBuffer"/> (HTTP endpoint / CLI source).
/// Registered only when <c>VAIS_DIAG_SPAN_BUFFER=true</c>. Dev-only, lossy, single-silo.
/// </summary>
internal sealed class DiagSpanBuffer : BaseExporter<Activity>, IDiagSpanBuffer
{
    private readonly int _capacity;
    private readonly ConcurrentQueue<DiagSpanRecord> _queue = new();

    internal DiagSpanBuffer(int capacity = 1000)
    {
        _capacity = capacity;
    }

    public bool IsEnabled => true;

    public IReadOnlyList<DiagSpanRecord> GetSpans(string? source, int limit)
    {
        var snapshot = _queue.ToArray();

        IEnumerable<DiagSpanRecord> filtered = snapshot;
        if (!string.IsNullOrWhiteSpace(source))
            filtered = filtered.Where(s => string.Equals(s.Source, source, StringComparison.OrdinalIgnoreCase));

        return filtered
            .Reverse()          // newest first
            .Take(limit)
            .ToList();
    }

    public override ExportResult Export(in Batch<Activity> batch)
    {
        foreach (var activity in batch)
        {
            var attrs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var tag in activity.TagObjects)
            {
                if (tag.Value is not null)
                    attrs[tag.Key] = tag.Value.ToString() ?? string.Empty;
            }

            _queue.Enqueue(new DiagSpanRecord(
                TraceId: activity.TraceId.ToHexString(),
                SpanId: activity.SpanId.ToHexString(),
                ParentSpanId: activity.ParentSpanId == default ? null : activity.ParentSpanId.ToHexString(),
                Name: activity.OperationName,
                Source: activity.Source.Name,
                StartTime: activity.StartTimeUtc,
                DurationMs: (long)activity.Duration.TotalMilliseconds,
                Status: activity.Status.ToString(),
                Attributes: attrs));

            // Drop oldest entry when over capacity
            while (_queue.Count > _capacity)
                _queue.TryDequeue(out _);
        }

        return ExportResult.Success;
    }
}
