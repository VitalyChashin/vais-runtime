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

    /// <summary>
    /// Registers <see cref="ToolRetryMiddleware"/> as a named factory under the key <c>"ToolRetry"</c>.
    /// Reads <c>maxAttempts</c> (default: 3) and <c>initialDelayMs</c> (default: 200) from
    /// <see cref="GatewayMiddlewareSpec.Params"/>.
    /// </summary>
    public static IServiceCollection AddNamedToolGatewayMiddleware_ToolRetry(
        this IServiceCollection services)
        => services.AddSingleton(new NamedToolGatewayMiddlewareRegistration(
            "ToolRetry",
            (spec, _) =>
            {
                var maxAttempts = spec.Params is { } p
                    && p.TryGetProperty("maxAttempts", out var a) ? a.GetInt32() : 3;
                var initialDelayMs = spec.Params is { } p2
                    && p2.TryGetProperty("initialDelayMs", out var d) ? d.GetInt32() : 200;
                return new ToolRetryMiddleware(maxAttempts, TimeSpan.FromMilliseconds(initialDelayMs));
            }));

    /// <summary>
    /// Registers <see cref="ToolTimeoutGuard"/> as a named factory under the key <c>"ToolTimeout"</c>.
    /// Reads <c>timeoutMs</c> (default: 30000) from <see cref="GatewayMiddlewareSpec.Params"/>.
    /// </summary>
    public static IServiceCollection AddNamedToolGatewayMiddleware_ToolTimeout(
        this IServiceCollection services)
        => services.AddSingleton(new NamedToolGatewayMiddlewareRegistration(
            "ToolTimeout",
            (spec, _) =>
            {
                var timeoutMs = spec.Params is { } p
                    && p.TryGetProperty("timeoutMs", out var v) ? v.GetInt32() : 30000;
                return new ToolTimeoutGuard(TimeSpan.FromMilliseconds(timeoutMs));
            }));

    /// <summary>
    /// Registers <see cref="ToolCircuitBreakerMiddleware"/> as a named factory under the key
    /// <c>"ToolCircuitBreaker"</c>. Reads <c>failureThreshold</c> (default: 5) and
    /// <c>openDurationMs</c> (default: 30000) from <see cref="GatewayMiddlewareSpec.Params"/>.
    /// </summary>
    public static IServiceCollection AddNamedToolGatewayMiddleware_ToolCircuitBreaker(
        this IServiceCollection services)
        => services.AddSingleton(new NamedToolGatewayMiddlewareRegistration(
            "ToolCircuitBreaker",
            (spec, _) =>
            {
                var threshold = spec.Params is { } p
                    && p.TryGetProperty("failureThreshold", out var f) ? f.GetInt32() : 5;
                var openDurationMs = spec.Params is { } p2
                    && p2.TryGetProperty("openDurationMs", out var o) ? o.GetInt32() : 30000;
                return new ToolCircuitBreakerMiddleware(threshold,
                    TimeSpan.FromMilliseconds(openDurationMs));
            }));
}
