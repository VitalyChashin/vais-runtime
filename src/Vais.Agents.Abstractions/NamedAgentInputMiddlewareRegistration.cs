// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Descriptor that maps a middleware name (as it appears in <see cref="GatewayMiddlewareSpec.Name"/>)
/// to a factory function. Phase-2 cognitive primitive packages register their middleware
/// names via <c>AddNamedAgentInputMiddleware</c> DI extensions.
/// The <c>DefaultAgentInputMiddlewareFactory</c> in <c>Vais.Agents.Core</c> collects all
/// registrations and dispatches by name at agent activation time.
/// </summary>
/// <param name="Name">Case-insensitive name that matches <see cref="GatewayMiddlewareSpec.Name"/>.</param>
/// <param name="Factory">
/// Creates the middleware for a given spec and service provider.
/// Called once per agent activation (not once per request).
/// </param>
public sealed record NamedAgentInputMiddlewareRegistration(
    string Name,
    Func<GatewayMiddlewareSpec, IServiceProvider, AgentInputMiddleware> Factory);
