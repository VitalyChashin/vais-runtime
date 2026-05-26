// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;

namespace Vais.Agents.Core;

/// <summary>
/// In-memory <see cref="IInterceptorTeeStore"/> backed by a bounded ring buffer. The store
/// keeps the most recent <see cref="Capacity"/> events and drops older ones to bound
/// memory. Thread-safe under concurrent <see cref="AppendAsync"/>; <see cref="QueryAsync"/>
/// reads a snapshot so concurrent appends never corrupt enumeration.
/// </summary>
/// <remarks>
/// Suitable for development / single-process runs and for test scenarios. Persistent
/// production deployments should register the Postgres-backed store instead (Plan D-4).
/// </remarks>
public sealed class InMemoryInterceptorTeeStore : IInterceptorTeeStore
{
    /// <summary>Default ring capacity (10,000 events) — covers a few minutes of busy traffic in dev.</summary>
    public const int DefaultCapacity = 10_000;

    private readonly Lock _gate = new();
    private readonly TrajectoryEvent[] _ring;
    private int _writeIndex;
    private int _count;

    /// <summary>Max events the ring holds before evicting the oldest.</summary>
    public int Capacity => _ring.Length;

    /// <summary>Current number of events in the ring (≤ <see cref="Capacity"/>).</summary>
    public int Count
    {
        get { lock (_gate) return _count; }
    }

    /// <summary>Build a store with <see cref="DefaultCapacity"/> ring size.</summary>
    public InMemoryInterceptorTeeStore() : this(DefaultCapacity) { }

    /// <summary>Build a store with the supplied ring capacity. Must be &gt; 0.</summary>
    public InMemoryInterceptorTeeStore(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity), "capacity must be positive");
        _ring = new TrajectoryEvent[capacity];
    }

    /// <inheritdoc />
    public ValueTask AppendAsync(TrajectoryEvent trajectoryEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trajectoryEvent);
        lock (_gate)
        {
            _ring[_writeIndex] = trajectoryEvent;
            _writeIndex = (_writeIndex + 1) % _ring.Length;
            if (_count < _ring.Length) _count++;
        }
        return default;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TrajectoryEvent> QueryAsync(
        TrajectoryQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        TrajectoryEvent[] snapshot;
        lock (_gate)
        {
            snapshot = new TrajectoryEvent[_count];
            // Walk newest-to-oldest by stepping backwards from the write pointer.
            var idx = (_writeIndex - 1 + _ring.Length) % _ring.Length;
            for (var i = 0; i < _count; i++)
            {
                snapshot[i] = _ring[idx];
                idx = (idx - 1 + _ring.Length) % _ring.Length;
            }
        }

        var yielded = 0;
        var limit = query.Limit ?? int.MaxValue;
        foreach (var evt in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Matches(evt, query)) continue;
            yield return evt;
            yielded++;
            if (yielded >= limit) yield break;
        }
        await Task.CompletedTask;
    }

    private static bool Matches(TrajectoryEvent evt, TrajectoryQuery query)
    {
        if (query.AgentId is { } a && !string.Equals(evt.AgentId, a, StringComparison.Ordinal)) return false;
        if (query.RunId is { } r && !string.Equals(evt.RunId, r, StringComparison.Ordinal)) return false;
        if (query.ConceptName is { } c && !string.Equals(evt.ConceptName, c, StringComparison.Ordinal)) return false;
        if (query.Transport is { } t && !string.Equals(evt.Transport, t, StringComparison.Ordinal)) return false;
        if (query.Since is { } since && evt.Timestamp < since) return false;
        if (query.Until is { } until && evt.Timestamp >= until) return false;
        if (query.OutcomeKind is { } ok && evt.Outcome?.Kind != ok) return false;
        return true;
    }
}
