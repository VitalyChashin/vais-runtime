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
        Budget         = ReadBudget(),
        RunId          = ActivityPropagation.ReadGraphRunId(),
        Scopes         = RequestContext.Get(AgenticTags.Scopes) as string[],
        BaselineRunId  = RequestContext.Get(AgenticTags.BaselineRunId) as string,
    };

    // Reassemble RunBudget from the per-field RequestContext primitives written by
    // OrleansOutgoingActivityFilter. Returns null when no Budget fields were set —
    // distinguishes "no budget" from "all fields are unlimited."
    private static RunBudget? ReadBudget()
    {
        var maxTurns           = RequestContext.Get(AgenticTags.BudgetMaxTurns)           as int?;
        var maxToolCalls       = RequestContext.Get(AgenticTags.BudgetMaxToolCalls)       as int?;
        var maxPromptTokens    = RequestContext.Get(AgenticTags.BudgetMaxPromptTokens)    as int?;
        var maxCompletionToks  = RequestContext.Get(AgenticTags.BudgetMaxCompletionTokens) as int?;
        var maxDurationTicks   = RequestContext.Get(AgenticTags.BudgetMaxDurationTicks)   as long?;

        if (maxTurns is null && maxToolCalls is null
            && maxPromptTokens is null && maxCompletionToks is null
            && maxDurationTicks is null)
            return null;

        return new RunBudget(
            MaxTurns: maxTurns,
            MaxToolCalls: maxToolCalls,
            MaxPromptTokens: maxPromptTokens,
            MaxCompletionTokens: maxCompletionToks,
            MaxDuration: maxDurationTicks is { } ticks ? TimeSpan.FromTicks(ticks) : null);
    }
}
