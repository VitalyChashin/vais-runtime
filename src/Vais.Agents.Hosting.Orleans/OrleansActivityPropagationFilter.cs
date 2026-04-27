// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Immutable;
using System.Diagnostics;
using Vais.Agents.Core;

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Outgoing grain call filter that propagates the current <see cref="Activity"/> trace context
/// and RCB fields from <see cref="IAgentContextAccessor"/> into <see cref="RequestContext"/>
/// so that grain-side spans are parented correctly and RCB enforcement is available in callee grains.
/// </summary>
/// <remarks>
/// Registered via <c>silo.AddOutgoingGrainCallFilter&lt;OrleansOutgoingActivityFilter&gt;()</c>
/// in <c>CompositionRoot.ConfigureSilo</c>. Trace context is read via <see cref="ActivityPropagation.ReadContext"/>;
/// RCB fields are read via <see cref="OrleansAgentContextAccessor"/> (or the in-process
/// <c>AsyncLocalAgentContextAccessor</c> when registered by the HTTP host).
/// </remarks>
internal sealed class OrleansOutgoingActivityFilter : IOutgoingGrainCallFilter
{
    private readonly IAgentContextAccessor _contextAccessor;

    public OrleansOutgoingActivityFilter(IAgentContextAccessor contextAccessor)
    {
        _contextAccessor = contextAccessor;
    }

    public async Task Invoke(IOutgoingGrainCallContext context)
    {
        // OTel trace propagation (existing behaviour).
        var current = Activity.Current;
        if (current?.Id is { } traceParent)
        {
            RequestContext.Set(ActivityPropagation.TraceParentKey, traceParent);
            if (!string.IsNullOrEmpty(current.TraceStateString))
                RequestContext.Set(ActivityPropagation.TraceStateKey, current.TraceStateString);
            if (current.GetTagItem("graph.run_id") is string runId)
                RequestContext.Set(ActivityPropagation.GraphRunIdKey, runId);
        }

        // RCB propagation — write non-null RCB fields to RequestContext so that
        // OrleansAgentContextAccessor on the callee side can reconstruct them.
        var ctx = _contextAccessor.Current;
        if (ctx.WorkspaceId is not null)
            RequestContext.Set(AgenticTags.WorkspaceId, ctx.WorkspaceId);
        if (ctx.PrivilegeLevel is { } pl)
            RequestContext.Set(AgenticTags.PrivilegeLevel, (int)pl);
        if (ctx.AutonomyLevel is { } al)
            RequestContext.Set(AgenticTags.AutonomyLevel, (int)al);
        if (ctx.AllowedTools is not null)
            RequestContext.Set(AgenticTags.AllowedTools,
                ctx.AllowedTools is ImmutableHashSet<string> hs ? hs : ctx.AllowedTools.ToImmutableHashSet());
        if (ctx.MaxChainDepth is not null)
            RequestContext.Set(AgenticTags.MaxChainDepth, ctx.MaxChainDepth);

        await context.Invoke();
    }
}

/// <summary>
/// <see cref="ISiloBuilder"/> extension to register activity propagation filters.
/// </summary>
public static class OrleansActivityPropagationExtensions
{
    /// <summary>
    /// Register the outgoing grain call filter that propagates <see cref="Activity"/> trace context
    /// through Orleans grain boundaries so that grain spans nest under their caller's span in Langfuse.
    /// </summary>
    public static ISiloBuilder AddOrleansActivityPropagation(this ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        return silo.AddOutgoingGrainCallFilter<OrleansOutgoingActivityFilter>();
    }
}

/// <summary>
/// Shared helpers for reading the propagated trace context inside grain methods.
/// </summary>
internal static class ActivityPropagation
{
    internal const string TraceParentKey = "vais.traceparent";
    internal const string TraceStateKey  = "vais.tracestate";
    internal const string GraphRunIdKey  = "vais.graph.run_id";

    /// <summary>
    /// Reads the W3C traceparent from <see cref="RequestContext"/> and returns the
    /// parsed <see cref="ActivityContext"/>, or <see langword="default"/> if absent or unparsable.
    /// </summary>
    internal static ActivityContext ReadContext()
    {
        var traceParent = RequestContext.Get(TraceParentKey) as string;
        if (traceParent is null)
            return default;

        var traceState = RequestContext.Get(TraceStateKey) as string;
        return ActivityContext.TryParse(traceParent, traceState, out var ctx) ? ctx : default;
    }

    /// <summary>
    /// Reads the <c>graph.run_id</c> propagated from the caller's span tags via
    /// <see cref="RequestContext"/>, or <see langword="null"/> if absent.
    /// </summary>
    internal static string? ReadGraphRunId() =>
        RequestContext.Get(GraphRunIdKey) as string;
}
