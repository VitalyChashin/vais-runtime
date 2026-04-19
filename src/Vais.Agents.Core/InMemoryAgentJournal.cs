// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Vais.Agents.Core;

/// <summary>
/// Thread-safe in-process <see cref="IAgentJournal"/>. Partitions entries by
/// <see cref="JournalEntry.RunId"/>; append operations on the same run serialise
/// via a per-run lock so read order is stable, and appends on different runs
/// proceed concurrently. Good enough for tests, dev loops, and the built-in
/// smoketest. Production workloads should wire a persistent backend (the Orleans
/// journal grain lands in a later PR; Postgres/Redis adapters are deferred).
/// </summary>
public sealed class InMemoryAgentJournal : IAgentJournal
{
    private readonly ConcurrentDictionary<string, RunLog> _runs = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask AppendAsync(JournalEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.RunId);

        var log = _runs.GetOrAdd(entry.RunId, _ => new RunLog());
        lock (log.Gate)
        {
            log.Entries.Add(entry);
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<JournalEntry> ReadAsync(
        string runId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        await Task.CompletedTask.ConfigureAwait(false);

        if (!_runs.TryGetValue(runId, out var log))
        {
            yield break;
        }

        // Snapshot under the lock so enumeration doesn't race with concurrent appends.
        JournalEntry[] snapshot;
        lock (log.Gate)
        {
            snapshot = log.Entries.ToArray();
        }

        foreach (var entry in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entry;
        }
    }

    /// <inheritdoc />
    public ValueTask ClearAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        _runs.TryRemove(runId, out _);
        return ValueTask.CompletedTask;
    }

    private sealed class RunLog
    {
        public readonly object Gate = new();
        public readonly List<JournalEntry> Entries = new();
    }
}
