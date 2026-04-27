// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;

namespace Vais.Agents.Core;

/// <summary>
/// DI extension methods for registering <see cref="ToolGatewayMiddleware"/> implementations.
/// </summary>
public static partial class ToolGatewayServiceCollectionExtensions
{
    /// <summary>
    /// Registers <typeparamref name="T"/> as a singleton <see cref="ToolGatewayMiddleware"/>.
    /// The manifest translator picks up all registered middleware and applies them
    /// (in registration order, outermost first) to every declarative agent's tool dispatch chain.
    /// </summary>
    public static IServiceCollection AddToolGatewayMiddleware<T>(
        this IServiceCollection services)
        where T : ToolGatewayMiddleware
    {
        services.AddSingleton<ToolGatewayMiddleware, T>();
        return services;
    }
}
