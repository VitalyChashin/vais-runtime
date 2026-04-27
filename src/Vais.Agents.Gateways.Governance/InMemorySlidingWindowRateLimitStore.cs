// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;

namespace Vais.Agents.Gateways.Governance;

/// <summary>
/// Thread-safe, in-process sliding-window <see cref="IRateLimitStore"/> implementation.
/// Accepts an optional <see cref="TimeProvider"/> for deterministic testing.
/// </summary>
public sealed class InMemorySlidingWindowRateLimitStore(TimeProvider? timeProvider = null) : IRateLimitStore
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private readonly ConcurrentDictionary<string, List<(DateTimeOffset Timestamp, int Tokens)>> _buckets = new();

    /// <inheritdoc/>
    public ValueTask<(int Requests, int Tokens)> RecordAndGetAsync(
        string key,
        int tokens,
        TimeSpan window,
        CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        var cutoff = now - window;

        var events = _buckets.GetOrAdd(key, _ => []);
        lock (events)
        {
            events.RemoveAll(e => e.Timestamp <= cutoff);
            events.Add((now, tokens));
            var totalRequests = events.Count;
            var totalTokens = events.Sum(e => e.Tokens);
            return ValueTask.FromResult((totalRequests, totalTokens));
        }
    }
}
