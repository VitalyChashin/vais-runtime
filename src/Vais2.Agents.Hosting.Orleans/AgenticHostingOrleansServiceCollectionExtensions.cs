// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vais2.Agents.Core;

namespace Vais2.Agents.Hosting.Orleans;

/// <summary>
/// DI extension methods for wiring the Orleans-backed agent runtime on the client side,
/// and for registering agent-grain dependencies on the silo side.
/// </summary>
public static class AgenticHostingOrleansServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="OrleansAgentRuntime"/> as a singleton <see cref="IAgentRuntime"/>
    /// plus an <see cref="OrleansAgentContextAccessor"/> that reads Orleans
    /// <c>RequestContext</c>. Call this on the <c>IServiceCollection</c> of the
    /// Orleans <em>client</em> (or combined silo+client host).
    /// </summary>
    /// <remarks>
    /// Silo-side grain dependencies (<see cref="ICompletionProvider"/>,
    /// <see cref="Func{String, StatefulAgentOptions}"/>) must additionally be registered
    /// on the silo via <see cref="ConfigureAgentGrains"/>.
    /// </remarks>
    /// <param name="services">The host's DI container.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddOrleansAgentRuntime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IAgentContextAccessor, OrleansAgentContextAccessor>();
        services.TryAddSingleton<IAgentRuntime>(sp => new OrleansAgentRuntime(sp.GetRequiredService<IGrainFactory>()));
        return services;
    }

    /// <summary>
    /// Register the silo-side dependencies required by <see cref="AiAgentGrain"/>:
    /// a <see cref="Func{String, StatefulAgentOptions}"/> that produces per-agent options.
    /// Expects <see cref="ICompletionProvider"/> to be registered separately (consumers
    /// choose between the SK and MAF adapter packages).
    /// </summary>
    /// <param name="services">The silo's DI container.</param>
    /// <param name="configureAgents">
    /// Optional. Given an agent id, returns the <see cref="StatefulAgentOptions"/> for that
    /// agent. Default: a plain options instance with <see cref="StatefulAgentOptions.AgentName"/>
    /// set to the id.
    /// </param>
    public static IServiceCollection ConfigureAgentGrains(
        this IServiceCollection services,
        Func<IServiceProvider, string, StatefulAgentOptions>? configureAgents = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<Func<string, StatefulAgentOptions>>(sp =>
            configureAgents is null
                ? id => new StatefulAgentOptions { AgentName = id }
                : id => configureAgents(sp, id));
        return services;
    }
}
