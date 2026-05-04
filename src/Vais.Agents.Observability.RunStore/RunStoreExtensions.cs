// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Vais.Agents.Observability.RunStore;

/// <summary>
/// Extension methods for registering the run store and optional Redis live-tail sink.
/// </summary>
public static class RunStoreExtensions
{
    /// <summary>
    /// Registers the Postgres-backed <see cref="IRunStore"/> and starts the
    /// <c>RunStoreSubscriber</c> hosted service that subscribes to the
    /// <see cref="Vais.Agents.IAgentGraphEventBus"/> and persists events.
    /// </summary>
    public static IServiceCollection AddRunStore(
        this IServiceCollection services,
        Action<RunStoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);

        services.AddSingleton<IRunStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<RunStoreOptions>>().Value;
            var logger = sp.GetRequiredService<ILogger<PostgresRunStore>>();
            return new PostgresRunStore(opts.ConnectionString, logger);
        });

        services.AddHostedService<RunStoreSubscriber>();

        return services;
    }

    /// <summary>
    /// Registers the Redis live-tail sink that mirrors graph events to a per-run
    /// Redis Stream (<c>vais:run:{runId}:events</c>, capped at 2000 entries).
    /// Call after <see cref="AddRunStore"/> when live SSE tailing is needed.
    /// </summary>
    public static IServiceCollection AddRunStoreRedisStream(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(connectionString);

        services.AddHostedService(sp =>
            new RedisRunStreamSink(
                sp.GetRequiredService<Vais.Agents.IAgentGraphEventBus>(),
                connectionString,
                sp.GetRequiredService<ILogger<RedisRunStreamSink>>()));

        return services;
    }
}
