// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Vais.Agents.Observability.McpEventStore;

/// <summary>Extension methods for registering the MCP event store.</summary>
public static class McpEventStoreExtensions
{
    /// <summary>
    /// Registers the Postgres-backed <see cref="IMcpEventStore"/>, a
    /// <see cref="Microsoft.Extensions.Hosting.IHostedService"/> that initialises the schema and applies
    /// retention on startup, a <see cref="Vais.Agents.ToolGatewayMiddleware"/> singleton (used by agents
    /// without an explicit <c>mcpGatewayRef</c>), and a named middleware factory under the key
    /// <c>"McpServerLogging"</c> (used by agents whose gateway manifest lists that middleware name).
    /// </summary>
    /// <remarks>
    /// The named factory tags all events with the single <see cref="McpEventStoreOptions.ServerId"/>
    /// from configuration. This is sufficient when there is one MCP server per deployment; a
    /// multi-server setup would require separate <c>AddMcpEventStore</c> registrations.
    /// </remarks>
    public static IServiceCollection AddMcpEventStore(
        this IServiceCollection services,
        Action<McpEventStoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);

        services.AddSingleton<IMcpEventStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<McpEventStoreOptions>>().Value;
            var logger = sp.GetRequiredService<ILogger<PostgresMcpEventStore>>();
            return new PostgresMcpEventStore(opts.ConnectionString, logger);
        });

        // Fallback path: agents without mcpGatewayRef get this singleton injected via GetServices<ToolGatewayMiddleware>.
        services.AddSingleton<Vais.Agents.ToolGatewayMiddleware>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<McpEventStoreOptions>>().Value;
            var store = sp.GetRequiredService<IMcpEventStore>();
            var ctx = sp.GetRequiredService<Vais.Agents.IAgentContextAccessor>();
            var mwLogger = sp.GetRequiredService<ILogger<McpEventMiddleware>>();
            return new McpEventMiddleware(store, ctx, opts.ServerId, mwLogger);
        });

        // Named path: agents whose mcpGatewayRef manifest includes "McpServerLogging" use this factory.
        services.AddSingleton(sp => new NamedToolGatewayMiddlewareRegistration(
            "McpServerLogging",
            (_, svcs) =>
            {
                var opts = svcs.GetRequiredService<IOptions<McpEventStoreOptions>>().Value;
                var store = svcs.GetRequiredService<IMcpEventStore>();
                var ctx = svcs.GetRequiredService<Vais.Agents.IAgentContextAccessor>();
                var mwLogger = svcs.GetRequiredService<ILogger<McpEventMiddleware>>();
                return new McpEventMiddleware(store, ctx, opts.ServerId, mwLogger);
            }));

        services.AddHostedService<McpEventStoreInitializer>();

        return services;
    }
}
