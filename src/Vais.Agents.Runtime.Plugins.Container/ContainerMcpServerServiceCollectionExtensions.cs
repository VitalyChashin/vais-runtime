// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vais.Agents.Control.Mcp;

namespace Vais.Agents.Runtime.Plugins.Container;

/// <summary>
/// DI entry point for container-supervised MCP servers (the <c>transport: containerStdio</c> path).
/// Mirror of <see cref="PhysicalMcpServiceCollectionExtensions.AddPhysicalMcpServers"/> for the
/// container-supervised case.
/// </summary>
public static class ContainerMcpServerServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="ContainerMcpServerHostService"/> as an <see cref="IHostedService"/>
    /// and as an <see cref="INamedToolSourceProvider"/>. The service scans
    /// <see cref="IMcpServerRegistry"/> for entries with <c>transport: containerStdio</c>,
    /// supervises one container per server via <see cref="DockerContainerSupervisor"/>
    /// or <see cref="KubernetesContainerSupervisor"/>, and opens an MCP streamableHttp
    /// connection to each container's bridge endpoint.
    /// </summary>
    /// <remarks>
    /// Requires <see cref="ContainerPluginLoaderOptions"/> to be configured in DI (typically by
    /// <c>AddContainerPlugins</c>). Container MCP servers respect the same resource bounds and
    /// <c>VAIS_DOCKER_PLUGIN_NETWORK</c> setting as container plugins.
    /// </remarks>
    public static IServiceCollection AddContainerMcpServers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ContainerMcpServerHostService>(sp =>
            new ContainerMcpServerHostService(
                sp.GetRequiredService<IMcpServerRegistry>(),
                sp.GetRequiredService<ContainerPluginLoaderOptions>(),
                sp,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()));

        services.AddSingleton<INamedToolSourceProvider>(sp =>
            sp.GetRequiredService<ContainerMcpServerHostService>());

        services.AddSingleton<IContainerMcpServerHost>(sp =>
            sp.GetRequiredService<ContainerMcpServerHostService>());

        services.AddHostedService(sp =>
            sp.GetRequiredService<ContainerMcpServerHostService>());

        return services;
    }
}
