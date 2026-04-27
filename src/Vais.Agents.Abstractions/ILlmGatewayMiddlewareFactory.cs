// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Resolves a <see cref="GatewayMiddlewareSpec.Name"/> to a concrete
/// <see cref="LlmGatewayMiddleware"/> instance. Each <c>Vais.Agents.Gateways.*</c>
/// package registers its middleware name(s) via
/// <c>services.AddNamedLlmGatewayMiddleware("X", factory)</c>. The default composite
/// implementation delegates to all registered named factories in registration order.
/// </summary>
public interface ILlmGatewayMiddlewareFactory
{
    /// <summary>
    /// Creates middleware for <paramref name="spec"/>.
    /// Throws <see cref="InvalidOperationException"/> if <paramref name="spec"/>'s name is unknown.
    /// </summary>
    LlmGatewayMiddleware Create(GatewayMiddlewareSpec spec);
}
