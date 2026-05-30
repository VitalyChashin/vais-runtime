// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Vais.Agents.Observability.RunHealthStore;

/// <summary>
/// DI helpers to register the Postgres run-health store and the event-bus subscriber that
/// persists mechanical-failure signals (and, on startup, creates the schema + applies retention).
/// </summary>
public static class RunHealthStoreExtensions
{
    /// <summary>
    /// Registers the Postgres-backed <see cref="IRunHealthStore"/> and the
    /// <see cref="RunHealthSignalSubscriber"/> hosted service, which subscribes to
    /// <c>IAgentEventBus</c>, initialises the schema, and prunes old rows on startup.
    /// Mirrors <c>AddAgentRunStore</c>'s options-delegate shape.
    /// </summary>
    public static IServiceCollection AddRunHealthStore(
        this IServiceCollection services,
        Action<RunHealthStoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);

        services.AddSingleton<IRunHealthStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<RunHealthStoreOptions>>().Value;
            return new PostgresRunHealthStore(opts.ConnectionString);
        });

        services.AddHostedService<RunHealthSignalSubscriber>();

        return services;
    }
}
