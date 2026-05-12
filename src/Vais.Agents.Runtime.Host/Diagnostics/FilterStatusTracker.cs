// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using Vais.Agents.Control;

namespace Vais.Agents.Runtime.Host.Diagnostics;

/// <summary>
/// Default <see cref="IFilterStatusTracker"/> — lock-free per-interface counters
/// incremented by <c>OrleansOutgoingActivityFilter</c>.
/// </summary>
internal sealed class FilterStatusTracker : IFilterStatusTracker
{
    private sealed class Counters
    {
        public long WithActivity;
        public long WithoutActivity;
    }

    private readonly ConcurrentDictionary<string, Counters> _map = new(StringComparer.Ordinal);

    public void RecordCall(string grainInterface, bool hasActivity)
    {
        var c = _map.GetOrAdd(grainInterface, static _ => new Counters());
        if (hasActivity)
            Interlocked.Increment(ref c.WithActivity);
        else
            Interlocked.Increment(ref c.WithoutActivity);
    }

    public IReadOnlyList<FilterCallEntry> GetSnapshot() =>
        _map
            .Select(kv => new FilterCallEntry(
                kv.Key,
                Interlocked.Read(ref kv.Value.WithActivity),
                Interlocked.Read(ref kv.Value.WithoutActivity)))
            .OrderByDescending(static e => e.WithActivity + e.WithoutActivity)
            .ToList();
}
