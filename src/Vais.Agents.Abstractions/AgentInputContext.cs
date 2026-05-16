// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Carries the inbound message and identity metadata for a single agent invocation,
/// available to every <see cref="AgentInputMiddleware"/> in the chain.
/// Middleware may mutate <see cref="Message"/> to shape the input; all other properties
/// are immutable and identify the invocation context.
/// </summary>
public sealed class AgentInputContext
{
    /// <summary>The stable agent identifier for this invocation.</summary>
    public required string AgentId { get; init; }

    /// <summary>The run identifier stamped on this turn, or null when no run id is available.</summary>
    public string? RunId { get; init; }

    /// <summary>
    /// The graph node identifier, populated only when the invocation originates from a
    /// <c>GraphNodeExecutor</c> Agent-kind node. Null for direct grain calls.
    /// </summary>
    public string? NodeId { get; init; }

    /// <summary>
    /// The inbound message text. Middleware may replace this value to shape the input before
    /// the agent receives it. The final value after the chain resolves is passed to the agent.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Mutable property bag for passing shaping state between middleware layers.
    /// Phase-2 cognitive primitives (HCM, S-MMU, DIEE, PAS) write enrichment data here;
    /// downstream middleware and the Phase-2 infrastructure read from it.
    /// </summary>
    public Dictionary<string, object?> Properties { get; } = new(StringComparer.Ordinal);
}
