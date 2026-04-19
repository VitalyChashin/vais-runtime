// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Vais.Agents.Protocols.Mcp.Server;

/// <summary>
/// DI helpers for wiring the MCP agent server into a host. Consumers separately
/// register <see cref="IAgentRegistry"/> + <see cref="IAgentLifecycleManager"/>
/// via their preferred stack (typically <c>Vais.Agents.Control.InProcess</c>'s
/// <c>AgentLifecycleManager</c>).
/// </summary>
public static class McpAgentServerServiceCollectionExtensions
{
    /// <summary>
    /// Register the stdio-hosted MCP agent server. Add this to a console-app DI
    /// container alongside a registry + lifecycle manager, then <c>Run</c> the
    /// host — Claude Desktop (or any MCP stdio client) can now connect by
    /// spawning the built executable.
    /// </summary>
    public static IServiceCollection AddMcpAgentServerStdio(
        this IServiceCollection services,
        Action<McpAgentServerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new McpAgentServerOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.AddHostedService<StdioAgentServerHost>();
        return services;
    }

    /// <summary>
    /// Register just the builder so consumers can host the server over a
    /// transport of their choice (e.g. for tests using in-process streams).
    /// No hosted-service registration; pairs with <see cref="McpAgentServerBuilder.Build"/>.
    /// </summary>
    public static IServiceCollection AddMcpAgentServerBuilder(
        this IServiceCollection services,
        Action<McpAgentServerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new McpAgentServerOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        return services;
    }
}
