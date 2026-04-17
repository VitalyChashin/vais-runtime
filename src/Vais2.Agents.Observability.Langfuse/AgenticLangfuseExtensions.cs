// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Vais2.Agents.Observability.Langfuse;

/// <summary>
/// DI helpers for <see cref="LangfuseEnrichmentFilter"/>.
/// </summary>
public static class AgenticLangfuseExtensions
{
    /// <summary>
    /// Register <see cref="LangfuseEnrichmentFilter"/> and <see cref="LangfuseEnrichmentOptions"/>
    /// as singletons. The filter is also published as <see cref="IAgentFilter"/> so it is picked up
    /// by any code resolving the filter chain via DI. The consumer is still responsible for
    /// adding the filter to a specific agent's <c>StatefulAgentOptions.Filters</c> list.
    /// </summary>
    /// <param name="services">The target service collection.</param>
    /// <param name="options">
    /// Optional overrides. A fresh <see cref="LangfuseEnrichmentOptions"/> with defaults is used
    /// when null.
    /// </param>
    /// <returns>The same service collection for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    public static IServiceCollection AddLangfuseEnrichment(
        this IServiceCollection services,
        LangfuseEnrichmentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(options ?? new LangfuseEnrichmentOptions());
        services.TryAddSingleton<LangfuseEnrichmentFilter>();
        services.AddSingleton<IAgentFilter>(sp => sp.GetRequiredService<LangfuseEnrichmentFilter>());
        return services;
    }
}
