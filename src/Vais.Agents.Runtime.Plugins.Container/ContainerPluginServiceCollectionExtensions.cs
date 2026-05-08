// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;

namespace Vais.Agents.Runtime.Plugins.Container;

/// <summary>Extension methods for registering container plugin services.</summary>
public static class ContainerPluginServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ICallTokenService"/> and <see cref="ContainerPluginHostService"/>.
    /// The runtime host must also call
    /// <see cref="ContainerGatewayEndpointRouteBuilderExtensions.MapContainerGatewayEndpoints"/>
    /// on the internal port pipeline.
    /// </summary>
    public static IServiceCollection AddContainerPlugins(
        this IServiceCollection services,
        Action<ContainerPluginLoaderOptions>? configure = null)
    {
        var options = new ContainerPluginLoaderOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton<ICallTokenService, HmacCallTokenService>();
        services.AddHostedService<ContainerPluginHostService>();
        return services;
    }
}
