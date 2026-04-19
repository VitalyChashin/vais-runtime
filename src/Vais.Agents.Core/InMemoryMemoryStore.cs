// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Vais.Agents.Core;

/// <summary>
/// Thread-safe in-process <see cref="IMemoryStore"/>. Partitions items by the full
/// <see cref="MemoryScope"/> record (relying on record value equality). Search uses
/// naive case-insensitive substring matching — good enough for tests, dev loops,
/// and the built-in smoketest. Production workloads should wire a vector-backed
/// implementation instead.
/// </summary>
public sealed class InMemoryMemoryStore : IMemoryStore
{
    private readonly ConcurrentDictionary<MemoryScope, ConcurrentDictionary<string, MemoryItem>> _partitions = new();

    /// <inheritdoc />
    public ValueTask WriteAsync(MemoryScope scope, string key, MemoryItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(item);

        var partition = _partitions.GetOrAdd(scope, _ => new ConcurrentDictionary<string, MemoryItem>(StringComparer.Ordinal));
        partition[key] = item;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<MemoryItem?> ReadAsync(MemoryScope scope, string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_partitions.TryGetValue(scope, out var partition) && partition.TryGetValue(key, out var item))
        {
            return ValueTask.FromResult<MemoryItem?>(item);
        }
        return ValueTask.FromResult<MemoryItem?>(null);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<MemorySearchResult> SearchAsync(
        MemoryScope scope,
        string query,
        int topK = 5,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);

        await Task.CompletedTask.ConfigureAwait(false);

        if (topK <= 0 || !_partitions.TryGetValue(scope, out var partition))
        {
            yield break;
        }

        var yielded = 0;
        // Empty query = enumerate the partition in insertion order.
        var matchAll = query.Length == 0;

        foreach (var (key, item) in partition)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!matchAll &&
                item.Content.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            yield return new MemorySearchResult(key, item, Score: null);
            yielded++;
            if (yielded >= topK)
            {
                yield break;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> DeleteAsync(MemoryScope scope, string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_partitions.TryGetValue(scope, out var partition))
        {
            return ValueTask.FromResult(partition.TryRemove(key, out _));
        }
        return ValueTask.FromResult(false);
    }
}
