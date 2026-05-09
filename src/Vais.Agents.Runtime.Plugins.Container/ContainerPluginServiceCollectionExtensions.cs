// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vais.Agents.Runtime.Instantiation;
using Vais.Agents.Runtime.Plugins.Container.Preprocessing;
using Vais.Agents.Runtime.Plugins;

namespace Vais.Agents.Runtime.Plugins.Container;

/// <summary>Extension methods for registering container plugin services.</summary>
public static class ContainerPluginServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ICallTokenService"/>, <see cref="ContainerPluginHostService"/>,
    /// and the built-in preprocessing pipeline (<see cref="IAgentPreprocessor"/> chain).
    /// The runtime host must also call
    /// <see cref="ContainerGatewayEndpointRouteBuilderExtensions.MapContainerGatewayEndpoints"/>
    /// on the internal port pipeline.
    /// </summary>
    public static IServiceCollection AddContainerPlugins(
        this IServiceCollection services,
        Action<ContainerPluginLoaderOptions>? configure = null)
    {
        services.EnsurePluginRegistry();
        var options = new ContainerPluginLoaderOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton<ICallTokenService, HmacCallTokenService>();
        services.AddSingleton<ContainerPluginHostService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ContainerPluginHostService>());
        services.AddSingleton<IContainerPluginHost>(sp => sp.GetRequiredService<ContainerPluginHostService>());
        services.AddSingleton<IContainerPluginReloader>(sp =>
            new DefaultContainerPluginReloader(
                sp.GetRequiredService<ContainerPluginHostService>(),
                sp.GetService<ILogger<DefaultContainerPluginReloader>>()));
        services.AddSingleton<IAgentPreprocessor, HistoryAssembler>();
        services.AddSingleton<IAgentPreprocessor>(sp => new SystemPromptInjector(
            sp.GetService<IPromptTemplateRegistry>(),
            sp.GetService<IPromptFileLoader>()));
        return services;
    }

    /// <summary>
    /// Registers a custom <see cref="IAgentPreprocessor"/> implementation to run in the
    /// container plugin preprocessing pipeline. The built-in preprocessors always run first
    /// (Order 0 and 10); register custom preprocessors at Order &gt;= 100 to run after them.
    /// Phase 2 use: memory injection, policy enforcement.
    /// </summary>
    public static IServiceCollection AddAgentPreprocessor<T>(
        this IServiceCollection services)
        where T : class, IAgentPreprocessor
    {
        services.AddSingleton<IAgentPreprocessor, T>();
        return services;
    }
}
