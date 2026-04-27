// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Base class for composable tool call gateway middleware. Override <see cref="InvokeAsync"/>
/// to intercept outbound tool dispatch. Call <c>next</c> to pass through;
/// do NOT call <c>next</c> to short-circuit (return a cached result, deny the
/// call, etc.).
/// </summary>
/// <remarks>
/// Instances must be reentrant — do not store per-call state in instance fields; use
/// local variables inside <see cref="InvokeAsync"/> instead.
/// The chain runs before all <see cref="IToolGuardrail"/> hooks; guardrails remain as the
/// inner layer and are preserved for backwards compatibility.
/// Migration note: if an existing <see cref="IToolGuardrail"/> implementation needs
/// short-circuit capability, subclass <see cref="ToolGatewayMiddleware"/> instead and
/// return an outcome without calling <c>next</c>.
/// </remarks>
public abstract class ToolGatewayMiddleware
{
    /// <summary>
    /// Intercepts a tool dispatch. The default implementation is a pass-through.
    /// </summary>
    public virtual Task<ToolCallOutcome> InvokeAsync(
        ToolGatewayContext context,
        Func<Task<ToolCallOutcome>> next,
        CancellationToken cancellationToken = default)
        => next();
}
