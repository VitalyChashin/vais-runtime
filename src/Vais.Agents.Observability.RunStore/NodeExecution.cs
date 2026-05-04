// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Observability.RunStore;

/// <summary>Snapshot of a single node execution within a pipeline run.</summary>
/// <param name="RunId">Parent run identifier.</param>
/// <param name="NodeId">Graph node identifier.</param>
/// <param name="NodeKind">Node type (e.g. <c>agent</c>, <c>interrupt</c>).</param>
/// <param name="AgentId">Agent identifier for agent-kind nodes, <see langword="null"/> otherwise.</param>
/// <param name="Status">Current lifecycle status.</param>
/// <param name="StartedAt">UTC timestamp when the node started.</param>
/// <param name="EndedAt">UTC timestamp when the node ended, or <see langword="null"/> if still running.</param>
/// <param name="DurationMs">Elapsed milliseconds, or <see langword="null"/> if still running.</param>
/// <param name="InputText">Truncated input text sent to the agent (max 8 KB), or <see langword="null"/>.</param>
/// <param name="OutputText">Truncated output text returned by the agent (max 8 KB), or <see langword="null"/>.</param>
/// <param name="InputTokens">Approximate input token count (0 if not tracked).</param>
/// <param name="OutputTokens">Approximate output token count (0 if not tracked).</param>
/// <param name="Error">Error message if the node failed, <see langword="null"/> otherwise.</param>
/// <param name="EdgesTaken">Outgoing edge targets traversed after this node completed.</param>
public sealed record NodeExecution(
    string RunId,
    string NodeId,
    string NodeKind,
    string? AgentId,
    RunStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    long? DurationMs,
    string? InputText,
    string? OutputText,
    int InputTokens,
    int OutputTokens,
    string? Error,
    IReadOnlyList<string>? EdgesTaken);
