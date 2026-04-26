// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Outgoing grain call filter that propagates the current <see cref="Activity"/> trace context
/// into <see cref="RequestContext"/> so that grain-side spans (grain.ask, grain.activate)
/// can be parented to the caller's span (e.g. graph.node from InProcessGraphOrchestrator).
/// </summary>
/// <remarks>
/// Registered via <c>silo.AddOutgoingGrainCallFilter&lt;OrleansOutgoingActivityFilter&gt;()</c>
/// in <c>CompositionRoot.ConfigureSilo</c>. The grain reads the keys via
/// <see cref="ActivityPropagation.ReadContext"/>.
/// </remarks>
internal sealed class OrleansOutgoingActivityFilter : IOutgoingGrainCallFilter
{
    public async Task Invoke(IOutgoingGrainCallContext context)
    {
        var current = Activity.Current;
        if (current?.Id is { } traceParent)
        {
            RequestContext.Set(ActivityPropagation.TraceParentKey, traceParent);
            if (!string.IsNullOrEmpty(current.TraceStateString))
                RequestContext.Set(ActivityPropagation.TraceStateKey, current.TraceStateString);
        }
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
}
