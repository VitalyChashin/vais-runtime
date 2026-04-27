// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;

namespace Vais.Agents.Gateways.McpReliability;

/// <summary>DI extension methods for McpReliability gateway middleware.</summary>
public static class McpReliabilityServiceCollectionExtensions
{
    /// <summary>Registers <see cref="ToolRetryMiddleware"/> as a singleton gateway middleware.</summary>
    public static IServiceCollection AddToolRetryMiddleware(
        this IServiceCollection services,
        int maxAttempts = 3,
        TimeSpan? initialDelay = null)
    {
        services.AddSingleton<ToolGatewayMiddleware>(
            new ToolRetryMiddleware(maxAttempts, initialDelay));
        return services;
    }

    /// <summary>Registers <see cref="ToolTimeoutGuard"/> as a singleton gateway middleware.</summary>
    public static IServiceCollection AddToolTimeoutGuard(
        this IServiceCollection services,
        TimeSpan timeout)
    {
        services.AddSingleton<ToolGatewayMiddleware>(new ToolTimeoutGuard(timeout));
        return services;
    }

    /// <summary>Registers <see cref="ToolCircuitBreakerMiddleware"/> as a singleton gateway middleware.</summary>
    public static IServiceCollection AddToolCircuitBreakerMiddleware(
        this IServiceCollection services,
        int failureThreshold = 5,
        TimeSpan? resetTimeout = null)
    {
        services.AddSingleton<ToolGatewayMiddleware>(
            new ToolCircuitBreakerMiddleware(failureThreshold, resetTimeout));
        return services;
    }
}
