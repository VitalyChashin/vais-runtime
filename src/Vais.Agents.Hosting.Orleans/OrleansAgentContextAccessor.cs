// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Immutable;
using Vais.Agents.Core;

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// <see cref="IAgentContextAccessor"/> implementation that reads Orleans'
/// <see cref="RequestContext"/>. Use in the silo when a grain turn needs to
/// observe the caller's user / tenant / correlation id for telemetry, filters,
/// or authorisation.
/// </summary>
/// <remarks>
/// The keys this accessor reads match the <c>vais.*</c> tag names in
/// <see cref="AgenticTags"/>, so a value set by an ingress handler via
/// <c>RequestContext.Set(AgenticTags.UserId, "...")</c> will surface both on the
/// <see cref="AgentContext"/> and on any OTel activity the agent emits for this turn.
/// RCB fields (<see cref="AgentContext.WorkspaceId"/> etc.) are written to
/// <see cref="RequestContext"/> by <c>OrleansOutgoingActivityFilter</c> on each
/// outgoing grain call and are read back here on the callee side.
/// </remarks>
public sealed class OrleansAgentContextAccessor : IAgentContextAccessor
{
    /// <inheritdoc />
    public AgentContext Current => new(
        UserId: RequestContext.Get(AgenticTags.UserId) as string,
        TenantId: RequestContext.Get(AgenticTags.TenantId) as string,
        CorrelationId: RequestContext.Get(AgenticTags.CorrelationId) as string,
        AgentName: RequestContext.Get(AgenticTags.AgentName) as string)
    {
        WorkspaceId    = RequestContext.Get(AgenticTags.WorkspaceId) as string,
        PrivilegeLevel = RequestContext.Get(AgenticTags.PrivilegeLevel) is int p ? (PrivilegeLevel)p : null,
        AutonomyLevel  = RequestContext.Get(AgenticTags.AutonomyLevel)  is int a ? (AutonomyLevel)a  : null,
        AllowedTools   = RequestContext.Get(AgenticTags.AllowedTools) as ImmutableHashSet<string>,
        MaxChainDepth  = RequestContext.Get(AgenticTags.MaxChainDepth) as int?,
    };
}
