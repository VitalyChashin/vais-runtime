// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;

namespace Vais.Agents.Control.InProcess;

/// <summary>
/// Process-local <see cref="IGraphRunCoordinator"/> — tracks runs in a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>. Correct for single-process / library hosts.
/// In a multi-silo cluster, callers on other silos see no runs; use the Orleans-backed
/// coordinator there (registered by the runtime in clustered mode).
/// </summary>
public sealed class InProcessGraphRunCoordinator : IGraphRunCoordinator
{
    private sealed class Record
    {
        public required string GraphId { get; init; }
        public required string Version { get; init; }
        public required DateTimeOffset StartedAt { get; init; }
        public volatile bool CancelRequested;
    }

    private readonly ConcurrentDictionary<string, Record> _runs = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<bool> TryStartAsync(string runId, string graphId, string version, CancellationToken ct = default)
    {
        var added = _runs.TryAdd(runId, new Record
        {
            GraphId = graphId,
            Version = version,
            StartedAt = DateTimeOffset.UtcNow,
        });
        return ValueTask.FromResult(added);
    }

    /// <inheritdoc />
    public ValueTask MarkActiveAsync(string runId, string graphId, string version, CancellationToken ct = default)
    {
        _runs[runId] = new Record
        {
            GraphId = graphId,
            Version = version,
            StartedAt = DateTimeOffset.UtcNow,
        };
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask RequestCancelAsync(string runId, CancellationToken ct = default)
    {
        if (_runs.TryGetValue(runId, out var record))
        {
            record.CancelRequested = true;
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<bool> IsCancelRequestedAsync(string runId, CancellationToken ct = default)
        => ValueTask.FromResult(_runs.TryGetValue(runId, out var record) && record.CancelRequested);

    /// <inheritdoc />
    public ValueTask CompleteAsync(string runId, GraphRunOutcome outcome, CancellationToken ct = default)
    {
        _runs.TryRemove(runId, out _);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<GraphRunSnapshot?> GetAsync(string runId, CancellationToken ct = default)
        => ValueTask.FromResult(_runs.TryGetValue(runId, out var r)
            ? new GraphRunSnapshot(runId, r.GraphId, r.Version, r.StartedAt, r.CancelRequested)
            : null);
}
