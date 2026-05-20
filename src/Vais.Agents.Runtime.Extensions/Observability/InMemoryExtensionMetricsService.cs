// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Vais.Agents.Runtime.Extensions;

/// <summary>
/// Listens to <c>vais_extension_handler_invoke_duration_seconds</c> measurements
/// and maintains a 5-minute sliding window of samples per (extension, handler, seam).
/// </summary>
internal sealed class InMemoryExtensionMetricsService : IExtensionMetricsService, IDisposable
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private readonly record struct Sample(DateTimeOffset At, double DurationSeconds, string Action);

    private readonly ConcurrentDictionary<(string Extension, string Handler, string Seam), ConcurrentQueue<Sample>> _samples = new();
    private readonly MeterListener _meterListener;

    public InMemoryExtensionMetricsService()
    {
        _meterListener = new MeterListener();
        _meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == ExtensionTelemetry.MeterName
                && instrument.Name == "vais_extension_handler_invoke_duration_seconds")
                listener.EnableMeasurementEvents(instrument);
        };
        _meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            string? ext = null, handler = null, seam = null, action = null;
            foreach (var kv in tags)
            {
                switch (kv.Key)
                {
                    case "extension": ext = kv.Value?.ToString(); break;
                    case "handler":   handler = kv.Value?.ToString(); break;
                    case "seam":      seam = kv.Value?.ToString(); break;
                    case "action":    action = kv.Value?.ToString(); break;
                }
            }
            if (ext is null || handler is null || seam is null || action is null) return;
            var queue = _samples.GetOrAdd((ext, handler, seam), _ => new ConcurrentQueue<Sample>());
            queue.Enqueue(new Sample(DateTimeOffset.UtcNow, value, action));
        });
        _meterListener.Start();
    }

    public ExtensionHandlerMetrics? GetMetrics(string extensionId)
    {
        var cutoff = DateTimeOffset.UtcNow - Window;
        var handlers = new List<HandlerMetrics>();

        foreach (var ((ext, handler, seam), queue) in _samples)
        {
            if (!string.Equals(ext, extensionId, StringComparison.OrdinalIgnoreCase)) continue;

            var fresh = queue.Where(s => s.At >= cutoff).ToList();
            if (fresh.Count == 0) continue;

            var durations = fresh.Select(s => s.DurationSeconds).OrderBy(d => d).ToList();
            handlers.Add(new HandlerMetrics(
                HandlerId: handler,
                Seam: seam,
                P50Seconds: Percentile(durations, 0.50),
                P95Seconds: Percentile(durations, 0.95),
                ErrorRate: (double)fresh.Count(s => s.Action is "fail" or "skip") / fresh.Count,
                TotalInvocations: fresh.Count));
        }

        if (handlers.Count == 0
            && !_samples.Keys.Any(k => string.Equals(k.Extension, extensionId, StringComparison.OrdinalIgnoreCase)))
            return null;

        return new ExtensionHandlerMetrics(extensionId, handlers);
    }

    public void Dispose() => _meterListener.Dispose();

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0.0;
        var idx = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }
}
