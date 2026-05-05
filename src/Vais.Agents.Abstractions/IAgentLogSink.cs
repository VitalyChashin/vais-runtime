// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Receives captured log lines from agent grains and Python plugin subprocesses.
/// Exposed via <c>GET /v1/agents/{id}/logs</c> so the Workbench can display
/// live stdout without tailing container logs.
/// </summary>
/// <remarks>
/// Implementations must be thread-safe — multiple grains and supervisors may call
/// <see cref="Add"/> concurrently. <see cref="Add"/> must not throw.
/// </remarks>
public interface IAgentLogSink
{
    /// <summary>Appends a log entry to the per-agent buffer. Silently drops if the buffer is full.</summary>
    void Add(AgentLogEntry entry);

    /// <summary>
    /// Returns recent log entries for an agent, ordered newest-first.
    /// </summary>
    /// <param name="agentId">Agent identifier to query.</param>
    /// <param name="since">Inclusive lower bound on <see cref="AgentLogEntry.At"/>; <see langword="null"/> for no lower bound.</param>
    /// <param name="limit">Maximum number of entries to return.</param>
    IReadOnlyList<AgentLogEntry> GetLogs(string agentId, DateTimeOffset? since = null, int limit = 100);
}
