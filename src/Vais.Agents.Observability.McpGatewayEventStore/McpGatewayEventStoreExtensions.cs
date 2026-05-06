// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Vais.Agents.Observability.McpGatewayEventStore;

/// <summary>Extension methods for registering the MCP gateway event store.</summary>
public static class McpGatewayEventStoreExtensions
{
    /// <summary>
    /// Registers the Postgres-backed <see cref="IMcpGatewayEventStore"/>, a
    /// <see cref="Microsoft.Extensions.Hosting.IHostedService"/> that initialises the schema and applies
    /// retention on startup, a <see cref="Vais.Agents.ToolGatewayMiddleware"/> singleton (used by agents
    /// without an explicit <c>mcpGatewayRef</c>), and a named middleware factory under the key
    /// <c>"McpGatewayLogging"</c> (used by agents whose gateway manifest lists that middleware name).
    /// </summary>
    public static IServiceCollection AddMcpGatewayEventStore(
        this IServiceCollection services,
        Action<McpGatewayEventStoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);

        services.AddSingleton<IMcpGatewayEventStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<McpGatewayEventStoreOptions>>().Value;
            var logger = sp.GetRequiredService<ILogger<PostgresMcpGatewayEventStore>>();
            return new PostgresMcpGatewayEventStore(opts.ConnectionString, logger);
        });

        // Fallback path: agents without mcpGatewayRef get this singleton injected via GetServices<ToolGatewayMiddleware>.
        services.AddSingleton<Vais.Agents.ToolGatewayMiddleware>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<McpGatewayEventStoreOptions>>().Value;
            var store = sp.GetRequiredService<IMcpGatewayEventStore>();
            var ctx = sp.GetRequiredService<Vais.Agents.IAgentContextAccessor>();
            var mwLogger = sp.GetRequiredService<ILogger<McpGatewayEventMiddleware>>();
            return new McpGatewayEventMiddleware(store, ctx, opts.GatewayId, mwLogger);
        });

        // Named path: agents whose mcpGatewayRef manifest includes "McpGatewayLogging" use this factory.
        services.AddSingleton(sp => new NamedToolGatewayMiddlewareRegistration(
            "McpGatewayLogging",
            (_, svcs) =>
            {
                var opts = svcs.GetRequiredService<IOptions<McpGatewayEventStoreOptions>>().Value;
                var store = svcs.GetRequiredService<IMcpGatewayEventStore>();
                var ctx = svcs.GetRequiredService<Vais.Agents.IAgentContextAccessor>();
                var mwLogger = svcs.GetRequiredService<ILogger<McpGatewayEventMiddleware>>();
                return new McpGatewayEventMiddleware(store, ctx, opts.GatewayId, mwLogger);
            }));

        services.AddHostedService<McpGatewayEventStoreInitializer>();

        return services;
    }
}
