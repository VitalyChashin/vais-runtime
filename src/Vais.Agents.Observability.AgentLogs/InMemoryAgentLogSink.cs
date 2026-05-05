// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;

namespace Vais.Agents.Observability.AgentLogs;

/// <summary>
/// In-memory ring-buffer implementation of <see cref="IAgentLogSink"/>.
/// One queue per agent, capped at <see cref="AgentLogSinkOptions.BufferLinesPerAgent"/> entries.
/// </summary>
internal sealed class InMemoryAgentLogSink : IAgentLogSink
{
    private readonly int _cap;
    private readonly ConcurrentDictionary<string, Queue<AgentLogEntry>> _buffers =
        new(StringComparer.Ordinal);

    internal InMemoryAgentLogSink(int cap) => _cap = cap;

    public void Add(AgentLogEntry entry)
    {
        var queue = _buffers.GetOrAdd(entry.AgentId, _ => new Queue<AgentLogEntry>());
        lock (queue)
        {
            queue.Enqueue(entry);
            while (queue.Count > _cap)
                queue.Dequeue();
        }
    }

    public IReadOnlyList<AgentLogEntry> GetLogs(string agentId, DateTimeOffset? since = null, int limit = 100)
    {
        if (!_buffers.TryGetValue(agentId, out var queue))
            return [];

        lock (queue)
        {
            IEnumerable<AgentLogEntry> entries = queue;
            if (since.HasValue)
                entries = entries.Where(e => e.At >= since.Value);
            // queue is FIFO (oldest first) — TakeLast gives most recent N, Reverse gives newest-first
            return entries.TakeLast(limit).Reverse().ToArray();
        }
    }
}
