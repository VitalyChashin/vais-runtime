// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Ambient context for a single agent turn. Read-only snapshot; not thread-shared
/// outside of the turn that created it.
/// </summary>
/// <param name="UserId">Optional user identity driving the turn.</param>
/// <param name="TenantId">Optional tenant / project identity.</param>
/// <param name="CorrelationId">Optional correlation id for cross-service tracing.</param>
/// <param name="AgentName">Optional stable identifier for the agent in use.</param>
public sealed record AgentContext(
    string? UserId = null,
    string? TenantId = null,
    string? CorrelationId = null,
    string? AgentName = null)
{
    /// <summary>An empty context with all fields null. Identity value for defaults.</summary>
    public static readonly AgentContext Empty = new();

    /// <summary>
    /// Opaque run identifier stamped by <c>StatefulAiAgent</c> for the duration of a
    /// single run. When non-null, the default tool-call dispatcher scopes
    /// <see cref="IAgentJournal"/> reads and writes to this run so the same tool call,
    /// replayed after a crash or pause, returns its previously recorded outcome
    /// instead of re-invoking the tool.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Left null by default to preserve pre-v0.5 behaviour — consumers who don't
    /// wire <c>StatefulAgentOptions.Journal</c> keep the point-in-time execution
    /// they had before. <c>StatefulAiAgent</c> fills this automatically starting
    /// in v0.5 PR 3.
    /// </para>
    /// </remarks>
    public string? RunId { get; init; }
}
