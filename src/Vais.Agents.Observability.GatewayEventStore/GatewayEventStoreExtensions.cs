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
    /// retention on startup, a <see cref="Vais.Agents.LlmGatewayMiddleware"/> singleton (used by agents
    /// without an explicit <c>llmGatewayRef</c>), and a named middleware factory under the key
    /// <c>"LlmGatewayLogging"</c> (used by agents whose gateway manifest lists that middleware name).
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

        // Fallback path: agents without llmGatewayRef get this singleton injected via GetServices<LlmGatewayMiddleware>.
        services.AddSingleton<Vais.Agents.LlmGatewayMiddleware>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<GatewayEventStoreOptions>>().Value;
            var store = sp.GetRequiredService<IGatewayEventStore>();
            var ctx = sp.GetRequiredService<Vais.Agents.IAgentContextAccessor>();
            var mwLogger = sp.GetRequiredService<ILogger<GatewayEventMiddleware>>();
            return new GatewayEventMiddleware(store, ctx, opts.GatewayId, mwLogger);
        });

        // Named path: agents whose llmGatewayRef manifest includes "LlmGatewayLogging" use this factory.
        services.AddSingleton(sp => new NamedLlmGatewayMiddlewareRegistration(
            "LlmGatewayLogging",
            (_, svcs) =>
            {
                var opts = svcs.GetRequiredService<IOptions<GatewayEventStoreOptions>>().Value;
                var store = svcs.GetRequiredService<IGatewayEventStore>();
                var ctx = svcs.GetRequiredService<Vais.Agents.IAgentContextAccessor>();
                var mwLogger = svcs.GetRequiredService<ILogger<GatewayEventMiddleware>>();
                return new GatewayEventMiddleware(store, ctx, opts.GatewayId, mwLogger);
            }));

        services.AddHostedService<GatewayEventStoreInitializer>();

        return services;
    }
}
