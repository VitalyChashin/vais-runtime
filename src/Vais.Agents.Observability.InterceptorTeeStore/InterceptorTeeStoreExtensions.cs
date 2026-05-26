// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Vais.Agents.Observability.InterceptorTeeStore;

/// <summary>DI entry points for the Postgres-backed interceptor tee store.</summary>
public static class InterceptorTeeStoreExtensions
{
    /// <summary>
    /// Register <see cref="PostgresInterceptorTeeStore"/> as the singleton
    /// <see cref="IInterceptorTeeStore"/> and an <see cref="IHostedService"/> that applies the
    /// schema + runs the retention prune at startup. Replaces any prior
    /// <see cref="IInterceptorTeeStore"/> registration (the runtime's in-memory default).
    /// </summary>
    public static IServiceCollection AddPostgresInterceptorTeeStore(
        this IServiceCollection services,
        Action<InterceptorTeeStoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);

        services.AddSingleton<IInterceptorTeeStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<InterceptorTeeStoreOptions>>().Value;
            var logger = sp.GetRequiredService<ILogger<PostgresInterceptorTeeStore>>();
            return new PostgresInterceptorTeeStore(opts.ConnectionString, logger);
        });

        services.AddHostedService<InterceptorTeeStoreInitializer>();
        return services;
    }
}
