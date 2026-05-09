// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vais.Agents.Runtime.Plugins;

/// <summary>
/// DI entry point for the plugin loader and optional hot-reload watcher.
/// </summary>
public static class PluginServiceCollectionExtensions
{
    /// <summary>
    /// Ensures <see cref="IPluginHandlerRegistry"/> is registered. Safe to call multiple
    /// times — subsequent calls are no-ops because <c>TryAddSingleton</c> is idempotent.
    /// Call this when you need the registry but do not want the assembly plugin loader
    /// (e.g. the container-plugins pillar when <c>VAIS_PLUGINS_DIRECTORY</c> is not set).
    /// </summary>
    public static IServiceCollection EnsurePluginRegistry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IPluginHandlerRegistry>(new PluginHandlerRegistry());
        return services;
    }

    /// <summary>
    /// Register the plugin loader + scan <paramref name="pluginsDirectory"/>
    /// at DI-build time. Loaded factories are registered in the singleton
    /// <see cref="IPluginHandlerRegistry"/>; the translator queries it
    /// when resolving <c>AgentHandlerRef.TypeName</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The directory scan happens lazily on first resolve of
    /// <see cref="IPluginHandlerRegistry"/>. Non-fatal per-plugin failures
    /// (missing DLL, ABI mismatch, convention miss) log a WARN and continue;
    /// fatal errors (handler-name collision with
    /// <see cref="PluginLoaderOptions.FailOnHandlerCollision"/> true,
    /// unreadable directory root) throw <see cref="PluginLoadException"/>.
    /// </para>
    /// <para>
    /// When <see cref="PluginLoaderOptions.ReloadPolicy"/> is
    /// <see cref="ReloadPolicy.DrainAndSwap"/>, this method additionally
    /// registers <see cref="IPluginReloader"/> and a background
    /// <see cref="IHostedService"/> that watches the directory for DLL
    /// changes and triggers hot-reloads after a 200 ms debounce.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddAgentPlugins(
        this IServiceCollection services,
        string pluginsDirectory,
        PluginLoaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsDirectory);

        var loaderOptions = options ?? new PluginLoaderOptions();
        var registry = new PluginHandlerRegistry();

        services.TryAddSingleton<IPluginHandlerRegistry>(sp =>
        {
            // Lazy-load on first resolve so the host's ILogger<AssemblyPluginLoader>
            // is available (logging services register later in the composition root).
            var logger = sp.GetService<ILogger<AssemblyPluginLoader>>();
            var loader = new AssemblyPluginLoader(loaderOptions, logger);
            loader.Load(pluginsDirectory, registry);
            return registry;
        });

        if (loaderOptions.ReloadPolicy == ReloadPolicy.DrainAndSwap)
        {
            services.TryAddSingleton<IPluginReloader>(sp =>
            {
                // Ensure registry is populated before the reloader is used.
                sp.GetRequiredService<IPluginHandlerRegistry>();
                var loaderLogger = sp.GetService<ILogger<AssemblyPluginLoader>>();
                var reloaderLogger = sp.GetService<ILogger<DefaultPluginReloader>>();
                var loader = new AssemblyPluginLoader(loaderOptions, loaderLogger);
                var hooks = sp.GetServices<IPluginReloadHook>();
                return new DefaultPluginReloader(loader, registry, hooks, reloaderLogger);
            });

            services.AddSingleton<IHostedService>(sp =>
            {
                var reloader = sp.GetRequiredService<IPluginReloader>();
                var lifetime = sp.GetRequiredService<IHostApplicationLifetime>();
                var logger = sp.GetService<ILogger<PluginWatcherService>>()
                    ?? NullLogger<PluginWatcherService>.Instance;
                return new PluginWatcherService(reloader, lifetime, pluginsDirectory, logger);
            });
        }

        return services;
    }
}
