// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Vais.Agents.Gateways.Governance;

namespace Vais.Agents.Gateways.McpGovernance;

/// <summary>DI extension methods for McpGovernance gateway middleware.</summary>
public static class McpGovernanceServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ToolRateLimitMiddleware"/> and <see cref="InMemorySlidingWindowRateLimitStore"/>
    /// as gateway middleware using the given options.
    /// </summary>
    public static IServiceCollection AddToolRateLimitMiddleware(
        this IServiceCollection services,
        ToolRateLimitOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<IRateLimitStore, InMemorySlidingWindowRateLimitStore>();
        services.AddSingleton<ToolGatewayMiddleware, ToolRateLimitMiddleware>();
        return services;
    }

    /// <summary>
    /// Registers <see cref="ToolWorkspacePolicyMiddleware"/> as a singleton gateway middleware
    /// with the given per-workspace policy map.
    /// </summary>
    public static IServiceCollection AddToolWorkspacePolicyMiddleware(
        this IServiceCollection services,
        IReadOnlyDictionary<string, WorkspaceToolPolicy> policies)
    {
        services.AddSingleton<ToolGatewayMiddleware>(
            new ToolWorkspacePolicyMiddleware(policies));
        return services;
    }
}
