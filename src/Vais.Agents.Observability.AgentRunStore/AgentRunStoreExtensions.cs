// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Vais.Agents.Observability.AgentRunStore;

/// <summary>Extension methods for registering the agent run store.</summary>
public static class AgentRunStoreExtensions
{
    /// <summary>
    /// Registers the Postgres-backed <see cref="IAgentRunStore"/> and an
    /// <see cref="IHostedService"/> that initialises the schema and applies retention on startup.
    /// </summary>
    public static IServiceCollection AddAgentRunStore(
        this IServiceCollection services,
        Action<AgentRunStoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);

        services.AddSingleton<IAgentRunStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<AgentRunStoreOptions>>().Value;
            var logger = sp.GetRequiredService<ILogger<PostgresAgentRunStore>>();
            return new PostgresAgentRunStore(opts.ConnectionString, logger);
        });

        services.AddHostedService<AgentRunStoreInitializer>();

        return services;
    }
}
