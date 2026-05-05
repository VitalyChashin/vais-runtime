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
    /// retention on startup, and a <see cref="Vais.Agents.ToolGatewayMiddleware"/> that records
    /// every MCP tool dispatch event.
    /// </summary>
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

        services.AddSingleton<Vais.Agents.ToolGatewayMiddleware>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<McpEventStoreOptions>>().Value;
            var store = sp.GetRequiredService<IMcpEventStore>();
            return new McpEventMiddleware(store, opts.ServerId);
        });

        services.AddHostedService<McpEventStoreInitializer>();

        return services;
    }
}
