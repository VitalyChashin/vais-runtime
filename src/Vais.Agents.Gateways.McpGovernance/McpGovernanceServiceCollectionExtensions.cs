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

    /// <summary>
    /// Registers <see cref="ToolRateLimitMiddleware"/> as a named factory under the key
    /// <c>"ToolRateLimit"</c>. Reads <c>callsPerMinute</c> from <see cref="GatewayMiddlewareSpec.Params"/>;
    /// defaults to 100 when absent. <see cref="IRateLimitStore"/> must be registered separately
    /// (e.g. via <see cref="AddToolRateLimitMiddleware"/>).
    /// </summary>
    public static IServiceCollection AddNamedToolGatewayMiddleware_ToolRateLimit(
        this IServiceCollection services)
        => services.AddSingleton(
            sp => new NamedToolGatewayMiddlewareRegistration(
                "ToolRateLimit",
                (spec, _) =>
                {
                    var callsPerMinute = spec.Params is { } p
                        && p.TryGetProperty("callsPerMinute", out var v) ? v.GetInt32() : 100;
                    var store = sp.GetService<IRateLimitStore>()
                        ?? new InMemorySlidingWindowRateLimitStore();
                    return new ToolRateLimitMiddleware(store,
                        new ToolRateLimitOptions { MaxRequestsPerWindow = callsPerMinute });
                }));

    /// <summary>
    /// Registers <see cref="ToolWorkspacePolicyMiddleware"/> as a named factory under the key
    /// <c>"ToolWorkspacePolicy"</c>. When referenced from an <c>McpGatewayConfig</c> middleware list,
    /// the <c>AgentManifestTranslator</c> constructs the middleware using
    /// <c>McpGatewayConfigManifest.WorkspacePolicies</c> rather than calling this factory.
    /// This registration is a sentinel so the factory recognises the name as valid; it falls back
    /// to an empty-policy (no-op) instance when workspace policies are absent.
    /// </summary>
    public static IServiceCollection AddNamedToolGatewayMiddleware_ToolWorkspacePolicy(
        this IServiceCollection services)
        => services.AddSingleton(new NamedToolGatewayMiddlewareRegistration(
            "ToolWorkspacePolicy",
            (_, _) => new ToolWorkspacePolicyMiddleware(
                new Dictionary<string, WorkspaceToolPolicy>())));
}
