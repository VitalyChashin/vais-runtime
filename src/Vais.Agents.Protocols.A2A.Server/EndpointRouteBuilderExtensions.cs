// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using A2A;
using A2A.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Vais.Agents.Protocols.A2A.Server;

/// <summary>
/// ASP.NET Core wiring for exposing Vais agents as A2A endpoints. Each agent surfaced by
/// the registry gets its own JSON-RPC route at <c>{BasePath}/{agentId}</c> and a discovery
/// card at <c>{BasePath}/{agentId}/.well-known/agent-card.json</c>.
/// </summary>
public static class A2AAgentServerEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Mount one A2A endpoint per agent registered in the host's <see cref="IAgentRegistry"/>.
    /// Requires prior <see cref="A2AAgentServerServiceCollectionExtensions.AddA2AAgentServer"/>.
    /// Walks the registry synchronously at startup to produce the route list.
    /// </summary>
    /// <param name="endpoints">Endpoint builder (e.g. returned by <c>WebApplication</c>).</param>
    /// <param name="baseUrl">
    /// Absolute base URL that will be written into each agent card's <see cref="AgentInterface.Url"/>.
    /// Falls back to a placeholder when null — clients receiving the card must rewrite the URL
    /// if they're behind a gateway that reverse-proxies on a different host.
    /// </param>
    /// <returns>The per-agent entries mounted — useful for tests + diagnostics.</returns>
    public static IReadOnlyList<A2AAgentServerEntry> MapA2AAgentServer(
        this IEndpointRouteBuilder endpoints,
        string? baseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var services = endpoints.ServiceProvider;
        var registry = services.GetRequiredService<IAgentRegistry>();
        var lifecycle = services.GetRequiredService<IAgentLifecycleManager>();
        var options = services.GetRequiredService<A2AAgentServerOptions>();
        var taskStore = services.GetService<ITaskStore>();
        var loggerFactory = services.GetService<ILoggerFactory>();

        var effectiveBaseUrl = baseUrl ?? "http://localhost";
        var entries = A2AAgentServerBuilder.BuildAsync(
            registry,
            lifecycle,
            effectiveBaseUrl,
            options,
            taskStore,
            loggerFactory)
            .GetAwaiter().GetResult();

        foreach (var entry in entries)
        {
            endpoints.MapA2A(entry.Server, entry.Route);
            endpoints.MapWellKnownAgentCard(entry.Card, entry.Route);
        }

        return entries;
    }
}
