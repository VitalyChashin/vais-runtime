// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

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
}
