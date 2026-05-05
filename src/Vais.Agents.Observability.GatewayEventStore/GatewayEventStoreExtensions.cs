// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Vais.Agents.Observability.GatewayEventStore;

/// <summary>Extension methods for registering the gateway event store.</summary>
public static class GatewayEventStoreExtensions
{
    /// <summary>
    /// Registers the Postgres-backed <see cref="IGatewayEventStore"/>, a
    /// <see cref="Microsoft.Extensions.Hosting.IHostedService"/> that initialises the schema and applies
    /// retention on startup, and a <see cref="Vais.Agents.LlmGatewayMiddleware"/> that records
    /// every LLM completion event.
    /// </summary>
    public static IServiceCollection AddGatewayEventStore(
        this IServiceCollection services,
        Action<GatewayEventStoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);

        services.AddSingleton<IGatewayEventStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<GatewayEventStoreOptions>>().Value;
            var logger = sp.GetRequiredService<ILogger<PostgresGatewayEventStore>>();
            return new PostgresGatewayEventStore(opts.ConnectionString, logger);
        });

        services.AddSingleton<Vais.Agents.LlmGatewayMiddleware>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<GatewayEventStoreOptions>>().Value;
            var store = sp.GetRequiredService<IGatewayEventStore>();
            var ctx = sp.GetRequiredService<Vais.Agents.IAgentContextAccessor>();
            return new GatewayEventMiddleware(store, ctx, opts.GatewayId);
        });

        services.AddHostedService<GatewayEventStoreInitializer>();

        return services;
    }
}
