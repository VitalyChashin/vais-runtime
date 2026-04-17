// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Vais2.Agents.Core;

namespace Vais2.Agents.Hosting.InMemory;

/// <summary>
/// DI extension methods for wiring the in-memory agent runtime.
/// </summary>
public static class AgenticHostingInMemoryServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="InMemoryAgentRuntime"/> as a singleton <see cref="IAgentRuntime"/>.
    /// Expects a <see cref="ICompletionProvider"/> to already be registered.
    /// </summary>
    /// <param name="services">The DI container.</param>
    /// <param name="configureAgents">
    /// Optional — given an agent id, supply <see cref="StatefulAgentOptions"/> for it.
    /// Default: a plain options instance with <see cref="StatefulAgentOptions.AgentName"/> set to the id.
    /// </param>
    public static IServiceCollection AddInMemoryAgentRuntime(
        this IServiceCollection services,
        Func<IServiceProvider, string, StatefulAgentOptions>? configureAgents = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IAgentRuntime>(sp =>
        {
            var provider = sp.GetRequiredService<ICompletionProvider>();
            var loggerFactory = sp.GetService<ILoggerFactory>();

            Func<string, StatefulAgentOptions> factory =
                configureAgents is null
                    ? id => new StatefulAgentOptions { AgentName = id }
                    : id => configureAgents(sp, id);

            return new InMemoryAgentRuntime(provider, factory, loggerFactory);
        });

        return services;
    }
}
