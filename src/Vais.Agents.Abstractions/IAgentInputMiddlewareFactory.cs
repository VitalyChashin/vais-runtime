// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Resolves a <see cref="GatewayMiddlewareSpec.Name"/> to a concrete
/// <see cref="AgentInputMiddleware"/> instance. Each Phase-2 cognitive primitive
/// registers its middleware name(s) via
/// <c>services.AddNamedAgentInputMiddleware("X", factory)</c>. The default composite
/// implementation delegates to all registered named factories in registration order.
/// </summary>
public interface IAgentInputMiddlewareFactory
{
    /// <summary>
    /// Creates middleware for <paramref name="spec"/>.
    /// Throws <see cref="InvalidOperationException"/> if <paramref name="spec"/>'s name is unknown.
    /// </summary>
    AgentInputMiddleware Create(GatewayMiddlewareSpec spec);
}
