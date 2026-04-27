// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

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

    /// <summary>
    /// Registers <see cref="LlmLoggingMiddleware"/> as a named factory under the key
    /// <c>"LlmLogging"</c>.
    /// </summary>
    public static IServiceCollection AddNamedLlmGatewayMiddleware_LlmLogging(
        this IServiceCollection services)
        => services.AddSingleton(
            sp => new NamedLlmGatewayMiddlewareRegistration(
                "LlmLogging",
                (_, _) => new LlmLoggingMiddleware(
                    sp.GetRequiredService<ILogger<LlmLoggingMiddleware>>())));

    /// <summary>
    /// Registers <see cref="LlmUsageMiddleware"/> as a named factory under the key
    /// <c>"LlmUsage"</c>.
    /// </summary>
    public static IServiceCollection AddNamedLlmGatewayMiddleware_LlmUsage(
        this IServiceCollection services)
        => services.AddSingleton(
            sp => new NamedLlmGatewayMiddlewareRegistration(
                "LlmUsage",
                (_, _) => new LlmUsageMiddleware(
                    sp.GetRequiredService<IUsageSink>(),
                    sp.GetRequiredService<IAgentContextAccessor>())));

    /// <summary>
    /// Registers <see cref="LlmOtelMiddleware"/> as a named factory under the key
    /// <c>"LlmOtel"</c>.
    /// </summary>
    public static IServiceCollection AddNamedLlmGatewayMiddleware_LlmOtel(
        this IServiceCollection services)
        => services.AddSingleton(
            sp => new NamedLlmGatewayMiddlewareRegistration(
                "LlmOtel",
                (_, _) => new LlmOtelMiddleware(
                    sp.GetRequiredService<IAgentContextAccessor>())));

    /// <summary>
    /// Registers <see cref="LlmPromptEnrichmentMiddleware"/> as a named factory under the key
    /// <c>"LlmPromptEnrichment"</c>. Reads <c>additionalContext</c> from
    /// <see cref="GatewayMiddlewareSpec.Params"/> and appends it as a suffix to the system prompt.
    /// </summary>
    public static IServiceCollection AddNamedLlmGatewayMiddleware_LlmPromptEnrichment(
        this IServiceCollection services)
        => services.AddSingleton(new NamedLlmGatewayMiddlewareRegistration(
            "LlmPromptEnrichment",
            (spec, _) =>
            {
                var suffix = spec.Params is { } p
                    && p.TryGetProperty("additionalContext", out var v)
                    ? v.GetString() ?? "" : "";
                return new LlmPromptEnrichmentMiddleware(suffix: suffix);
            }));

    /// <summary>
    /// Registers <see cref="DefaultLlmGatewayMiddlewareFactory"/> as the
    /// <see cref="ILlmGatewayMiddlewareFactory"/> singleton (no-op if already registered).
    /// Call this after all <c>AddNamedLlmGatewayMiddleware_*</c> registrations.
    /// </summary>
    public static IServiceCollection AddDefaultLlmGatewayMiddlewareFactory(
        this IServiceCollection services)
    {
        services.TryAddSingleton<ILlmGatewayMiddlewareFactory, DefaultLlmGatewayMiddlewareFactory>();
        return services;
    }
}
