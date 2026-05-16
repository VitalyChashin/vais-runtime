// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Immutable;

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Orleans serialisation surrogate for <see cref="AgentContext"/>.
/// </summary>
[GenerateSerializer]
public struct AgentContextSurrogate
{
    /// <summary>Optional user id.</summary>
    [Id(0)]
    public string? UserId;

    /// <summary>Optional tenant id.</summary>
    [Id(1)]
    public string? TenantId;

    /// <summary>Optional correlation id for cross-service tracing.</summary>
    [Id(2)]
    public string? CorrelationId;

    /// <summary>Optional stable agent name.</summary>
    [Id(3)]
    public string? AgentName;

    /// <summary>Optional run id stamped by <c>StatefulAiAgent</c> for durable execution (v0.5+).</summary>
    [Id(4)]
    public string? RunId;

    // RCB fields (v0.next). New [Id] ordinals are safe — old messages missing these fields
    // deserialise with null values, which means "no enforcement" per the RCB convention.

    /// <summary>Workspace id from <see cref="AgentContext.WorkspaceId"/>.</summary>
    [Id(5)]
    public string? WorkspaceId;

    /// <summary>Privilege level stored as int for enum-rename stability.</summary>
    [Id(6)]
    public int? PrivilegeLevel;

    /// <summary>Autonomy level stored as int for enum-rename stability.</summary>
    [Id(7)]
    public int? AutonomyLevel;

    /// <summary>Tool allow-list. <see cref="ImmutableHashSet{T}"/> is Orleans-serialisable and implements <see cref="IReadOnlySet{T}"/>.</summary>
    [Id(8)]
    public ImmutableHashSet<string>? AllowedTools;

    /// <summary>Maximum agent-as-tool chain depth.</summary>
    [Id(9)]
    public int? MaxChainDepth;

    /// <summary>Optional baseline run id for eval cached-replay mode.</summary>
    [Id(10)]
    public string? BaselineRunId;
}

/// <summary>
/// Converts between <see cref="AgentContext"/> and its Orleans-serialisable surrogate.
/// </summary>
[RegisterConverter]
public sealed class AgentContextSurrogateConverter : IConverter<AgentContext, AgentContextSurrogate>
{
    /// <inheritdoc />
    public AgentContext ConvertFromSurrogate(in AgentContextSurrogate surrogate) =>
        new AgentContext(
            UserId: surrogate.UserId,
            TenantId: surrogate.TenantId,
            CorrelationId: surrogate.CorrelationId,
            AgentName: surrogate.AgentName)
        {
            RunId         = surrogate.RunId,
            WorkspaceId   = surrogate.WorkspaceId,
            PrivilegeLevel = surrogate.PrivilegeLevel is int p ? (Vais.Agents.PrivilegeLevel)p : null,
            AutonomyLevel  = surrogate.AutonomyLevel  is int a ? (Vais.Agents.AutonomyLevel)a  : null,
            AllowedTools  = surrogate.AllowedTools,
            MaxChainDepth = surrogate.MaxChainDepth,
            BaselineRunId = surrogate.BaselineRunId,
        };

    /// <inheritdoc />
    public AgentContextSurrogate ConvertToSurrogate(in AgentContext value) =>
        new()
        {
            UserId         = value.UserId,
            TenantId       = value.TenantId,
            CorrelationId  = value.CorrelationId,
            AgentName      = value.AgentName,
            RunId          = value.RunId,
            WorkspaceId    = value.WorkspaceId,
            PrivilegeLevel = value.PrivilegeLevel is { } p ? (int)p : null,
            AutonomyLevel  = value.AutonomyLevel  is { } a ? (int)a : null,
            AllowedTools   = value.AllowedTools is ImmutableHashSet<string> hs
                ? hs
                : value.AllowedTools?.ToImmutableHashSet(),
            MaxChainDepth  = value.MaxChainDepth,
            BaselineRunId  = value.BaselineRunId,
        };
}
