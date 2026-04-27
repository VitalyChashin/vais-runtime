// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;

namespace Vais.Agents.Core;

/// <summary>
/// DI extension methods for registering <see cref="LlmGatewayMiddleware"/> implementations.
/// </summary>
public static partial class LlmGatewayServiceCollectionExtensions
{
    /// <summary>
    /// Registers <typeparamref name="T"/> as a singleton <see cref="LlmGatewayMiddleware"/>.
    /// The manifest translator picks up all registered middleware and prepends them
    /// (in registration order) to every declarative agent's filter chains.
    /// </summary>
    public static IServiceCollection AddLlmGatewayMiddleware<T>(
        this IServiceCollection services)
        where T : LlmGatewayMiddleware
    {
        services.AddSingleton<LlmGatewayMiddleware, T>();
        return services;
    }
}
