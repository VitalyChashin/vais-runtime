// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Vais.Agents.Core;

/// <summary>
/// DI extension methods for registering <see cref="AgentInputMiddleware"/> implementations.
/// </summary>
public static class AgentInputMiddlewareServiceCollectionExtensions
{
    /// <summary>
    /// Registers <typeparamref name="T"/> as a singleton <see cref="AgentInputMiddleware"/>.
    /// The manifest translator picks up all registered middleware and applies them
    /// (in registration order, outermost first) to every agent's input chain.
    /// </summary>
    public static IServiceCollection AddAgentInputMiddleware<T>(
        this IServiceCollection services)
        where T : AgentInputMiddleware
    {
        services.AddSingleton<AgentInputMiddleware, T>();
        return services;
    }

    /// <summary>
    /// Registers a named <see cref="AgentInputMiddleware"/> factory under <paramref name="name"/>.
    /// Phase-2 cognitive primitive packages call this to contribute their middleware names
    /// so the <see cref="DefaultAgentInputMiddlewareFactory"/> can resolve them.
    /// </summary>
    public static IServiceCollection AddNamedAgentInputMiddleware(
        this IServiceCollection services,
        string name,
        Func<GatewayMiddlewareSpec, IServiceProvider, AgentInputMiddleware> factory)
        => services.AddSingleton(new NamedAgentInputMiddlewareRegistration(name, factory));

    /// <summary>
    /// Registers <see cref="DefaultAgentInputMiddlewareFactory"/> as the
    /// <see cref="IAgentInputMiddlewareFactory"/> singleton (no-op if already registered).
    /// Call this after all <c>AddNamedAgentInputMiddleware</c> registrations.
    /// </summary>
    public static IServiceCollection AddDefaultAgentInputMiddlewareFactory(
        this IServiceCollection services)
    {
        services.TryAddSingleton<IAgentInputMiddlewareFactory, DefaultAgentInputMiddlewareFactory>();
        return services;
    }
}
