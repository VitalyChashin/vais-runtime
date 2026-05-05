// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Observability.AgentLogs;

/// <summary>Configuration options for the in-memory agent log sink.</summary>
public sealed class AgentLogSinkOptions
{
    /// <summary>
    /// Maximum number of log lines retained per agent. When the buffer is full, the oldest
    /// entries are evicted to make room. Configurable via <c>VAIS_AGENT_LOG_BUFFER_LINES</c>.
    /// </summary>
    public int BufferLinesPerAgent { get; set; } = 500;
}
