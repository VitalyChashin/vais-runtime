// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vais.Agents.Control;

namespace Vais.Agents.Runtime.Plugins.Python;

/// <summary>
/// DI entry point for the Python plugin host.
/// </summary>
public static class PythonPluginServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Python plugin host as a singleton <see cref="IHostedService"/> and
    /// <see cref="IPythonPluginHost"/>. At startup the host scans
    /// <see cref="PythonPluginLoaderOptions.PluginsDirectory"/>, spawns one subprocess per
    /// Python plugin, performs the MCP handshake, and marks each plugin Ready or Unavailable.
    /// </summary>
    /// <remarks>
    /// When <see cref="PythonPluginLoaderOptions.ReloadPolicy"/> is
    /// <see cref="ReloadPolicy.DrainAndSwap"/>, this method additionally registers
    /// <see cref="IPythonPluginReloader"/> and a background <see cref="IHostedService"/>
    /// that watches each plugin directory for <c>plugin.yaml</c>, <c>*.py</c>, and
    /// <c>pyproject.toml</c> changes and triggers in-place hot-reloads after a 200 ms debounce.
    /// </remarks>
    /// <param name="services">The DI container.</param>
    /// <param name="options">
    /// Loader options. When <see langword="null"/>, defaults are used
    /// (<c>/var/lib/vais/plugins</c>, ABI <c>0.23</c>, 5 s handshake timeout).
    /// </param>
    public static IServiceCollection AddPythonPlugins(
        this IServiceCollection services,
        PythonPluginLoaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var opts = options ?? new PythonPluginLoaderOptions();

        // Guard double-registration: only add the IHostedService forwarder once.
        var alreadyRegistered = services.Any(sd => sd.ServiceType == typeof(IPythonPluginHost));

        services.TryAddSingleton<IPythonPluginHost>(sp =>
        {
            var loggerFactory = sp.GetService<ILoggerFactory>();
            var handlerRegistry = sp.GetService<IPluginHandlerRegistry>();
            var secretResolver = sp.GetService<ISecretResolver>();
            return new PythonPluginHostService(opts, loggerFactory, handlerRegistry: handlerRegistry,
                secretResolver: secretResolver);
        });

        if (!alreadyRegistered)
        {
            services.AddSingleton<IHostedService>(sp =>
                (IHostedService)sp.GetRequiredService<IPythonPluginHost>());
            services.AddSingleton<INamedToolSourceProvider>(sp =>
                (INamedToolSourceProvider)sp.GetRequiredService<IPythonPluginHost>());
        }

        if (opts.ReloadPolicy == ReloadPolicy.DrainAndSwap)
        {
            services.TryAddSingleton<IPythonPluginReloader>(sp =>
            {
                // Resolve the concrete type so we can call TryGetSupervisor.
                var host = (PythonPluginHostService)sp.GetRequiredService<IPythonPluginHost>();
                var loggerFactory = sp.GetService<ILoggerFactory>();
                var drainTimeout = TimeSpan.FromSeconds(opts.ReloadDrainTimeoutSeconds);
                var secretResolver = sp.GetService<ISecretResolver>();
                return new DefaultPythonPluginReloader(host, opts, drainTimeout, loggerFactory,
                    secretResolver);
            });

            services.AddSingleton<IHostedService>(sp =>
            {
                var reloader = sp.GetRequiredService<IPythonPluginReloader>();
                var host = sp.GetRequiredService<IPythonPluginHost>();
                var lifetime = sp.GetRequiredService<IHostApplicationLifetime>();
                var logger = sp.GetService<ILogger<PythonPluginWatcherService>>();
                return new PythonPluginWatcherService(reloader, host, lifetime, logger);
            });
        }

        return services;
    }
}
