// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

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

    /// <summary>
    /// Registers <see cref="ToolLoggingMiddleware"/> as a named factory under the key
    /// <c>"ToolLogging"</c>.
    /// </summary>
    public static IServiceCollection AddNamedToolGatewayMiddleware_ToolLogging(
        this IServiceCollection services)
        => services.AddSingleton(
            sp => new NamedToolGatewayMiddlewareRegistration(
                "ToolLogging",
                (_, _) => new ToolLoggingMiddleware(
                    sp.GetRequiredService<ILogger<ToolLoggingMiddleware>>())));

    /// <summary>
    /// Registers <see cref="ToolOtelMiddleware"/> as a named factory under the key
    /// <c>"ToolOtel"</c>.
    /// </summary>
    public static IServiceCollection AddNamedToolGatewayMiddleware_ToolOtel(
        this IServiceCollection services)
        => services.AddSingleton(new NamedToolGatewayMiddlewareRegistration(
            "ToolOtel",
            (_, _) => new ToolOtelMiddleware()));

    /// <summary>
    /// Registers <see cref="ToolDenyFilterMiddleware"/> as a named factory under the key
    /// <c>"ToolDenyFilter"</c>. Reads <c>blockedToolNames</c> string array from
    /// <see cref="GatewayMiddlewareSpec.Params"/>.
    /// </summary>
    public static IServiceCollection AddNamedToolGatewayMiddleware_ToolDenyFilter(
        this IServiceCollection services)
        => services.AddSingleton(new NamedToolGatewayMiddlewareRegistration(
            "ToolDenyFilter",
            (spec, _) =>
            {
                var blocked = spec.Params is { } p
                    && p.TryGetProperty("blockedToolNames", out var arr)
                    && arr.ValueKind == JsonValueKind.Array
                    ? arr.EnumerateArray().Select(e => e.GetString()!).ToArray()
                    : (IReadOnlyList<string>)[];
                return new ToolDenyFilterMiddleware(blocked);
            }));

    /// <summary>
    /// Registers <see cref="ToolResponseTruncationMiddleware"/> as a named factory under the key
    /// <c>"ToolResponseTruncation"</c>. Reads <c>maxCharacters</c> (default: 4096) from
    /// <see cref="GatewayMiddlewareSpec.Params"/>.
    /// </summary>
    public static IServiceCollection AddNamedToolGatewayMiddleware_ToolResponseTruncation(
        this IServiceCollection services)
        => services.AddSingleton(new NamedToolGatewayMiddlewareRegistration(
            "ToolResponseTruncation",
            (spec, _) =>
            {
                var maxChars = spec.Params is { } p
                    && p.TryGetProperty("maxCharacters", out var v) ? v.GetInt32() : 4096;
                return new ToolResponseTruncationMiddleware(maxChars);
            }));

    /// <summary>
    /// Registers <see cref="DefaultToolGatewayMiddlewareFactory"/> as the
    /// <see cref="IToolGatewayMiddlewareFactory"/> singleton (no-op if already registered).
    /// Call this after all <c>AddNamedToolGatewayMiddleware_*</c> registrations.
    /// </summary>
    public static IServiceCollection AddDefaultToolGatewayMiddlewareFactory(
        this IServiceCollection services)
    {
        services.TryAddSingleton<IToolGatewayMiddlewareFactory, DefaultToolGatewayMiddlewareFactory>();
        return services;
    }
}
