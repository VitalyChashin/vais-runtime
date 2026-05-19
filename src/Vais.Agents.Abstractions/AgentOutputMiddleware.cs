// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Base class for composable agent output middleware. Override <see cref="InvokeAsync"/>
/// to observe or transform the LLM response before the agent processes it.
/// Call <c>next</c> to pass through; do NOT call <c>next</c> to short-circuit.
/// </summary>
/// <remarks>
/// Fires per LLM call (not per turn) — symmetric with <see cref="AgentInputMiddleware"/>.
/// During a tool-calling loop, fires once per LLM round-trip.
/// Instances must be reentrant — do not store per-call state in instance fields; use
/// local variables inside <see cref="InvokeAsync"/> instead.
/// </remarks>
public abstract class AgentOutputMiddleware
{
    /// <summary>
    /// Intercepts an LLM response. The default implementation is a pass-through.
    /// Read <see cref="AgentOutputContext.ResponseMessage"/> to inspect the response;
    /// write to <see cref="AgentOutputContext.Properties"/> to pass data downstream.
    /// </summary>
    public virtual Task InvokeAsync(
        AgentOutputContext context,
        Func<Task> next,
        CancellationToken cancellationToken = default)
        => next();
}
