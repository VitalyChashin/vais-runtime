// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Vais.Agents.Gateways.McpCache;

/// <summary>DI extension methods for McpCache gateway middleware.</summary>
public static class McpCacheServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="InMemoryToolResultCache"/> as <see cref="IToolResultCache"/> and
    /// <see cref="ToolResultCacheMiddleware"/> as gateway middleware.
    /// </summary>
    public static IServiceCollection AddInMemoryToolResultCache(
        this IServiceCollection services,
        IReadOnlyList<string>? excludedTools = null)
        => services
            .AddSingleton<IToolResultCache, InMemoryToolResultCache>()
            .AddSingleton<ToolGatewayMiddleware>(
                sp => new ToolResultCacheMiddleware(
                    sp.GetRequiredService<IToolResultCache>(), excludedTools));

    /// <summary>
    /// Registers <see cref="ToolResultCacheMiddleware"/> as a named factory under the key
    /// <c>"ToolResultCache"</c>. Reads <c>excludedTools</c> string array from
    /// <see cref="GatewayMiddlewareSpec.Params"/>. <see cref="IToolResultCache"/> must be
    /// registered separately (e.g. via <see cref="AddInMemoryToolResultCache"/>).
    /// </summary>
    public static IServiceCollection AddNamedToolGatewayMiddleware_ToolResultCache(
        this IServiceCollection services)
        => services.AddSingleton(
            sp => new NamedToolGatewayMiddlewareRegistration(
                "ToolResultCache",
                (spec, _) =>
                {
                    string[]? excluded = null;
                    if (spec.Params is { } p && p.TryGetProperty("excludedTools", out var arr)
                        && arr.ValueKind == JsonValueKind.Array)
                        excluded = arr.EnumerateArray().Select(e => e.GetString()!).ToArray();
                    return new ToolResultCacheMiddleware(
                        sp.GetRequiredService<IToolResultCache>(), excluded);
                }));
}
