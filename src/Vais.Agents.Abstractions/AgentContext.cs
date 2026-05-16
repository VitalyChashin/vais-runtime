// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json.Serialization;

namespace Vais.Agents;

/// <summary>
/// Privilege level assigned to an agent session. Governs which resources and tools
/// the agent may reach. Higher integer = lower privilege; this ordering lets
/// <c>Math.Max(caller, callee)</c> compute the effective level on agent-as-tool calls
/// (i.e. the most restrictive of the two wins).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PrivilegeLevel
{
    /// <summary>Platform-level access — widest set of permitted resources.</summary>
    Platform  = 0,
    /// <summary>Workspace-scoped access.</summary>
    Workspace = 1,
    /// <summary>Single-agent access — narrowest set of permitted resources.</summary>
    Agent     = 2,
}

/// <summary>
/// Autonomy level governing whether an agent may act without human review.
/// Higher integer = more restrictive; <c>Math.Max(caller, callee)</c> applies on
/// agent-as-tool calls so the most restrictive level always wins.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AutonomyLevel
{
    /// <summary>The agent acts immediately without requiring human approval.</summary>
    FullyAutonomous  = 0,
    /// <summary>The agent acts but a human is notified and may intervene.</summary>
    Supervised       = 1,
    /// <summary>Every action requires explicit human approval before execution.</summary>
    RequiresApproval = 2,
}

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
    /// Left null by default to preserve pre-v0.5 behaviour — consumers who don't
    /// wire <c>StatefulAgentOptions.Journal</c> keep the point-in-time execution
    /// they had before. <c>StatefulAiAgent</c> fills this automatically starting
    /// in v0.5 PR 3.
    /// </remarks>
    public string? RunId { get; init; }

    /// <summary>
    /// Optional baseline run id for eval cached-replay mode. When set, the tool-call
    /// dispatcher first looks up outcomes in the baseline run's journal by
    /// <c>(ToolName, Arguments)</c> match before invoking the tool live. Null = live
    /// invocation only.
    /// </summary>
    public string? BaselineRunId { get; init; }

    // ── Reasoning Control Block (RCB) fields ─────────────────────────────────
    // All fields are nullable; null = no enforcement. Gateway middleware and the
    // tool dispatcher treat null as "no constraint", preserving the pre-RCB
    // behaviour for deployments that do not set these fields. Private modules
    // that enforce multi-tenant policies set them from JWT claims in their auth
    // middleware.

    /// <summary>
    /// Workspace identifier within the tenant. More granular than <see cref="TenantId"/>;
    /// a single tenant may have many workspaces (teams, projects, environments).
    /// Used by workspace-scoped LLM routing and tool RBAC. <see langword="null"/> = no
    /// workspace scope applied.
    /// </summary>
    public string? WorkspaceId { get; init; }

    /// <summary>
    /// Privilege level for this session. <see langword="null"/> = no privilege restriction.
    /// On agent-as-tool calls the effective level is <c>Math.Max(caller, callee)</c>.
    /// </summary>
    public PrivilegeLevel? PrivilegeLevel { get; init; }

    /// <summary>
    /// Autonomy level for this session. <see langword="null"/> = no autonomy constraint.
    /// Schedulers and middleware may route based on this value; tool gateways may block
    /// actions that require approval when the level is <see cref="Agents.AutonomyLevel.FullyAutonomous"/>.
    /// </summary>
    public AutonomyLevel? AutonomyLevel { get; init; }

    /// <summary>
    /// Explicit tool allow-list. When non-<see langword="null"/>, the tool dispatcher
    /// rejects any invocation whose tool name is absent from this set.
    /// <see langword="null"/> = all tools permitted. An empty set = no tools permitted.
    /// </summary>
    public IReadOnlySet<string>? AllowedTools { get; init; }

    /// <summary>
    /// Hard limit on agent-as-tool chain depth. The outgoing grain call filter
    /// decrements a depth counter on each hop and rejects calls that would exceed
    /// this limit. <see langword="null"/> = unlimited.
    /// </summary>
    public int? MaxChainDepth { get; init; }
}
