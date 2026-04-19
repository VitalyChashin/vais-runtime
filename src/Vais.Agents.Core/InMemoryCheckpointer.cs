// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;

namespace Vais.Agents.Core;

/// <summary>
/// Concurrent-dictionary-backed <see cref="IGraphCheckpointer"/>. Dev + test default;
/// does NOT survive process restart. Durable persistence ships as <c>OrleansCheckpointer</c>
/// in <c>Vais.Agents.Hosting.Orleans</c> (same two-tier split as v0.8's task store).
/// </summary>
public sealed class InMemoryCheckpointer : IGraphCheckpointer
{
    private readonly ConcurrentDictionary<string, GraphCheckpoint> _checkpoints = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask SaveAsync(GraphCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        _checkpoints[checkpoint.RunId] = checkpoint;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<GraphCheckpoint?> LoadAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        return ValueTask.FromResult(_checkpoints.TryGetValue(runId, out var checkpoint) ? checkpoint : null);
    }

    /// <inheritdoc />
    public ValueTask DeleteAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        _checkpoints.TryRemove(runId, out _);
        return ValueTask.CompletedTask;
    }
}
