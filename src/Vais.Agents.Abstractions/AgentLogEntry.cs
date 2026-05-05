// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>A single captured log line from an agent grain or Python plugin subprocess.</summary>
/// <param name="EntryId">Unique identifier for this log entry.</param>
/// <param name="AgentId">Identifier of the agent that produced this entry.</param>
/// <param name="RunId">Correlation ID or graph run ID when available; <see langword="null"/> otherwise.</param>
/// <param name="At">UTC timestamp when the line was captured.</param>
/// <param name="Level">Log level label: <c>Trace</c>, <c>Debug</c>, <c>Information</c>, <c>Warning</c>, <c>Error</c>, or <c>Critical</c>.</param>
/// <param name="Message">The formatted log message text.</param>
/// <param name="Source">Origin: <c>grain</c> (from an Orleans agent grain) or <c>python</c> (from a Python subprocess).</param>
public sealed record AgentLogEntry(
    string EntryId,
    string AgentId,
    string? RunId,
    DateTimeOffset At,
    string Level,
    string Message,
    string Source);
