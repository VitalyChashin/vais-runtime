// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using A2A;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Vais.Agents.Protocols.A2A.Server;

/// <summary>
/// DI helpers for wiring the A2A agent server into a host. Consumers separately register
/// <see cref="IAgentRegistry"/> + <see cref="IAgentLifecycleManager"/> via their preferred
/// stack (typically <c>Vais.Agents.Control.InProcess</c>'s <c>AgentLifecycleManager</c>).
/// </summary>
public static class A2AAgentServerServiceCollectionExtensions
{
    /// <summary>
    /// Register the A2A agent server options + a default <see cref="InMemoryTaskStore"/> as
    /// the <see cref="ITaskStore"/>. Consumers override the task store by registering a
    /// different <see cref="ITaskStore"/> before calling this extension — <c>TryAddSingleton</c>
    /// preserves the prior registration.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configure">Optional options builder.</param>
    public static IServiceCollection AddA2AAgentServer(
        this IServiceCollection services,
        Action<A2AAgentServerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new A2AAgentServerOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<ITaskStore, InMemoryTaskStore>();
        return services;
    }
}
