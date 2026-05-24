// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Protocol;
using Vais.Agents.Control.Manifests;

namespace Vais.Agents.Control.Mcp.Server;

/// <summary>
/// ASP.NET Core wiring for the design-tools MCP server (read-only, Plan A).
/// Mount at a path distinct from <c>/mcp</c> (the agents-as-tools server).
/// </summary>
public static class DesignMcpServerExtensions
{
    /// <summary>
    /// Register the design-tools MCP server over the SDK's streamableHttp transport.
    /// Also registers <see cref="IOntologyCatalog"/> (from the embedded base ontology)
    /// as a singleton if not already registered.
    /// </summary>
    public static IServiceCollection AddMcpDesignServer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Register IOntologyCatalog using the embedded base-ontology.json.
        // TryAdd so a host can override it (e.g. with a deployment-local overlay).
        services.TryAddSingleton<IOntologyCatalog>(_ => OntologyCatalog.BuildFromEmbeddedBase());

        services
            .AddMcpServer(opts =>
            {
                opts.ServerInfo = new Implementation
                {
                    Name = "vais-design",
                    Version = "0.1.0",
                };
                opts.ServerInstructions =
                    "Read-only design tools for the Vais.Agents runtime. " +
                    "Use vais.list/get to browse registered resources, vais.describe to explore the ontology, " +
                    "vais.diff to compare a candidate manifest, and vais.validate to dry-run validate without mutating anything.";
                opts.Capabilities = new ServerCapabilities
                {
                    Tools = new ToolsCapability { ListChanged = false },
                    Resources = new ResourcesCapability { ListChanged = false },
                };
                opts.Handlers.ListToolsHandler = DesignMcpToolHandlers.HandleListToolsAsync;
                opts.Handlers.CallToolHandler = DesignMcpToolHandlers.HandleCallToolAsync;
                opts.Handlers.ListResourcesHandler = DesignMcpToolHandlers.HandleListResourcesAsync;
                opts.Handlers.ReadResourceHandler = DesignMcpToolHandlers.HandleReadResourceAsync;
            })
            // Stateless: all design tools are idempotent reads; no session state needed.
            // Avoids the "Session not found" error when two AddMcpServer() registrations
            // share the same session store in the runtime host.
            .WithHttpTransport(o => o.Stateless = true);

        return services;
    }

    /// <summary>
    /// Mount the design-tools MCP endpoint at <paramref name="pattern"/> (default <c>/design-mcp</c>).
    /// Call <see cref="AddMcpDesignServer"/> on the service collection first.
    /// </summary>
    public static IEndpointConventionBuilder MapMcpDesignServer(
        this IEndpointRouteBuilder builder,
        string pattern = "/design-mcp")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        return builder.MapMcp(pattern);
    }
}
