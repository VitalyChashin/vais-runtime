// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;

namespace Vais.Agents.Control.Mcp;

/// <summary>
/// DI entry point for physical MCP server connection management.
/// </summary>
public static class PhysicalMcpServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="PhysicalMcpConnectionService"/> as an <see cref="Microsoft.Extensions.Hosting.IHostedService"/>
    /// and as an <see cref="INamedToolSourceProvider"/>, connecting to all physical
    /// <c>streamableHttp</c> and <c>sse</c> servers registered in <see cref="IMcpServerRegistry"/>
    /// at startup.
    /// </summary>
    /// <remarks>
    /// Call <c>AddAgentManifestInstantiator()</c> in the same DI container to wire
    /// translator cache invalidation on reconnect.
    /// <see cref="IMcpServerRegistry"/> must also be registered (e.g. via
    /// <c>AddInMemoryMcpServerRegistry</c> or the Orleans-backed variant).
    /// </remarks>
    public static IServiceCollection AddPhysicalMcpServers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<PhysicalMcpConnectionService>(sp =>
            new PhysicalMcpConnectionService(
                sp.GetRequiredService<IMcpServerRegistry>(),
                sp.GetServices<IMcpServerConnectionChangedHook>(),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<PhysicalMcpConnectionService>>()));

        services.AddSingleton<INamedToolSourceProvider>(sp =>
            sp.GetRequiredService<PhysicalMcpConnectionService>());

        services.AddHostedService(sp =>
            sp.GetRequiredService<PhysicalMcpConnectionService>());

        return services;
    }
}
