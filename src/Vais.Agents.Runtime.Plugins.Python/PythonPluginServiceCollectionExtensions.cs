// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

        // Guard double-registration: only add the IHostedService forwarder once.
        var alreadyRegistered = services.Any(sd => sd.ServiceType == typeof(IPythonPluginHost));

        services.TryAddSingleton<IPythonPluginHost>(sp =>
        {
            var loggerFactory = sp.GetService<ILoggerFactory>();
            var handlerRegistry = sp.GetService<IPluginHandlerRegistry>();
            return new PythonPluginHostService(options, loggerFactory, handlerRegistry: handlerRegistry);
        });

        if (!alreadyRegistered)
        {
            services.AddSingleton<IHostedService>(sp =>
                (IHostedService)sp.GetRequiredService<IPythonPluginHost>());
            services.AddSingleton<INamedToolSourceProvider>(sp =>
                (INamedToolSourceProvider)sp.GetRequiredService<IPythonPluginHost>());
        }

        return services;
    }
}
