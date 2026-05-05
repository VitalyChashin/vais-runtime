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
    /// retention on startup, and a <see cref="Vais.Agents.ToolGatewayMiddleware"/> that records
    /// every MCP tool dispatch event for the gateway.
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

        services.AddSingleton<Vais.Agents.ToolGatewayMiddleware>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<McpGatewayEventStoreOptions>>().Value;
            var store = sp.GetRequiredService<IMcpGatewayEventStore>();
            return new McpGatewayEventMiddleware(store, opts.GatewayId);
        });

        services.AddHostedService<McpGatewayEventStoreInitializer>();

        return services;
    }
}
