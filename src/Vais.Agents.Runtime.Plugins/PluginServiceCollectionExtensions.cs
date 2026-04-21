// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Vais.Agents.Runtime.Plugins;

/// <summary>
/// DI entry point for the v0.18 Pillar C plugin loader.
/// </summary>
public static class PluginServiceCollectionExtensions
{
    /// <summary>
    /// Register the plugin loader + scan <paramref name="pluginsDirectory"/>
    /// at DI-build time. Loaded factories are registered in the singleton
    /// <see cref="IPluginHandlerRegistry"/>; the translator queries it
    /// when resolving <c>AgentHandlerRef.TypeName</c>.
    /// </summary>
    /// <remarks>
    /// The directory scan happens synchronously during the call. Non-fatal
    /// per-plugin failures (missing DLL, ABI mismatch, convention miss)
    /// log a WARN and continue; fatal errors (handler-name collision with
    /// <see cref="PluginLoaderOptions.FailOnHandlerCollision"/> true,
    /// unreadable directory root) throw
    /// <see cref="PluginLoadException"/>.
    /// </remarks>
    public static IServiceCollection AddAgentPlugins(
        this IServiceCollection services,
        string pluginsDirectory,
        PluginLoaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsDirectory);

        var registry = new PluginHandlerRegistry();
        var loaderOptions = options ?? new PluginLoaderOptions();

        services.TryAddSingleton<IPluginHandlerRegistry>(sp =>
        {
            // Lazy-load on first resolve so the host's ILogger<AssemblyPluginLoader>
            // is available (logging services register later in the composition root).
            var logger = sp.GetService<ILogger<AssemblyPluginLoader>>();
            var loader = new AssemblyPluginLoader(loaderOptions, logger);
            loader.Load(pluginsDirectory, registry);
            return registry;
        });

        return services;
    }
}
