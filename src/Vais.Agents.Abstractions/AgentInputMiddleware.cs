// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Base class for composable agent input middleware. Override <see cref="InvokeAsync"/>
/// to intercept and shape the inbound message before the agent receives it.
/// Call <c>next</c> to pass through; do NOT call <c>next</c> to short-circuit
/// (suppress the turn, substitute a canned response, etc.).
/// </summary>
/// <remarks>
/// Instances must be reentrant — do not store per-call state in instance fields; use
/// local variables inside <see cref="InvokeAsync"/> instead.
/// The chain runs before the agent's own input guardrails and the provider call.
/// Phase-2 cognitive primitives (HCM, S-MMU, DIEE, PAS) register as named middleware
/// via <see cref="IAgentInputMiddlewareFactory"/> without modifying plugin code.
/// </remarks>
public abstract class AgentInputMiddleware
{
    /// <summary>
    /// Intercepts an agent invocation. The default implementation is a pass-through.
    /// Mutate <see cref="AgentInputContext.Message"/> to reshape the input; set properties
    /// in <see cref="AgentInputContext.Properties"/> to pass data to downstream middleware.
    /// </summary>
    public virtual Task InvokeAsync(
        AgentInputContext context,
        Func<Task> next,
        CancellationToken cancellationToken = default)
        => next();
}
