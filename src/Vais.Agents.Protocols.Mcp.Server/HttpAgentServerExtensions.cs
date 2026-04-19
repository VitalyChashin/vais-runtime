// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Vais.Agents.Protocols.Mcp.Server;

/// <summary>
/// ASP.NET Core wiring for hosting an MCP agent server over the streamableHttp
/// transport. Consumers compose this with <see cref="AddMcpAgentServerJwtAuth"/>
/// + <c>UseAuthentication</c>/<c>UseAuthorization</c> + <see cref="MapMcpAgentServer"/>
/// to get a REST-fronted MCP endpoint the same way the v0.6 control plane maps
/// its surface.
/// </summary>
public static class HttpAgentServerExtensions
{
    /// <summary>
    /// Register the MCP agent server over the SDK's <c>AddMcpServer().WithHttpTransport()</c>
    /// DI pipeline, configured from our <see cref="McpAgentServerBuilder"/> handlers so the
    /// same agent-wrapping semantics apply to both stdio and HTTP consumers.
    /// </summary>
    public static IServiceCollection AddMcpAgentServerHttp(
        this IServiceCollection services,
        Action<McpAgentServerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new McpAgentServerOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        services
            .AddMcpServer(srvOptions =>
            {
                // Populate srvOptions from our builder each time the DI container resolves it.
                // The builder reads IAgentRegistry + IAgentLifecycleManager via closures — but
                // here we can't resolve them at configure time, so we defer by keeping the
                // registry/lifecycle references as service-resolved closures inside the handlers.
                srvOptions.ServerInfo = new ModelContextProtocol.Protocol.Implementation
                {
                    Name = options.Name,
                    Version = options.Version,
                };
                srvOptions.ServerInstructions = options.Instructions;
                srvOptions.Capabilities = new ModelContextProtocol.Protocol.ServerCapabilities
                {
                    Tools = new ModelContextProtocol.Protocol.ToolsCapability { ListChanged = true },
                    Resources = new ModelContextProtocol.Protocol.ResourcesCapability { ListChanged = true },
                };
                // List + Call handlers resolve their dependencies via the request-scoped
                // IServiceProvider injected into McpServerHandler delegates.
                srvOptions.Handlers.ListToolsHandler = async (ctx, ct) =>
                {
                    var registry = ctx.Services!.GetRequiredService<IAgentRegistry>();
                    return await McpAgentServerBuilder.HandleListToolsAsync(registry, options, ct).ConfigureAwait(false);
                };
                srvOptions.Handlers.CallToolHandler = async (ctx, ct) =>
                {
                    var registry = ctx.Services!.GetRequiredService<IAgentRegistry>();
                    var lifecycle = ctx.Services!.GetRequiredService<IAgentLifecycleManager>();
                    return await McpAgentServerBuilder.HandleCallToolAsync(registry, lifecycle, ctx.Params, ct).ConfigureAwait(false);
                };
                srvOptions.Handlers.ListResourcesHandler = async (ctx, ct) =>
                {
                    var registry = ctx.Services!.GetRequiredService<IAgentRegistry>();
                    return await McpAgentServerBuilder.HandleListResourcesAsync(registry, options, ct).ConfigureAwait(false);
                };
                srvOptions.Handlers.ReadResourceHandler = async (ctx, ct) =>
                {
                    var registry = ctx.Services!.GetRequiredService<IAgentRegistry>();
                    return await McpAgentServerBuilder.HandleReadResourceAsync(registry, ctx.Params, ct).ConfigureAwait(false);
                };
            })
            .WithHttpTransport();

        return services;
    }

    /// <summary>
    /// Register JWT bearer auth for the MCP HTTP endpoint. Accepts tokens from
    /// either <c>Authorization: Bearer …</c> (direct-call) or
    /// <c>X-Upstream-Authorization: Bearer …</c> (gateway-forwarded — ContextForge
    /// and similar hubs). Upstream header wins when both are present.
    /// </summary>
    public static IServiceCollection AddMcpAgentServerJwtAuth(
        this IServiceCollection services,
        Action<JwtBearerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(opts =>
            {
                configure(opts);
                // Dual-header support: let an upstream-forwarded token override the
                // gateway's own credential on Authorization.
                var prior = opts.Events?.OnMessageReceived;
                opts.Events ??= new JwtBearerEvents();
                opts.Events.OnMessageReceived = async ctx =>
                {
                    if (prior is not null) await prior(ctx).ConfigureAwait(false);
                    if (ctx.Token is null &&
                        ctx.Request.Headers.TryGetValue("X-Upstream-Authorization", out var upstream) &&
                        upstream.Count > 0)
                    {
                        var raw = upstream[0]!;
                        const string bearer = "Bearer ";
                        if (raw.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
                        {
                            ctx.Token = raw[bearer.Length..];
                        }
                    }
                    else if (ctx.Request.Headers.TryGetValue("X-Upstream-Authorization", out var upstreamOverride) &&
                             upstreamOverride.Count > 0)
                    {
                        // Upstream header present AND a token was already extracted from
                        // Authorization — upstream wins.
                        var raw = upstreamOverride[0]!;
                        const string bearer = "Bearer ";
                        if (raw.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
                        {
                            ctx.Token = raw[bearer.Length..];
                        }
                    }
                };
            });
        services.AddAuthorization();
        return services;
    }

    /// <summary>
    /// Mount the MCP HTTP endpoint at <paramref name="pattern"/> (default <c>/mcp</c>).
    /// Requires <see cref="AddMcpAgentServerHttp"/> to have been called on the service
    /// collection. When auth is wired via <see cref="AddMcpAgentServerJwtAuth"/>,
    /// make sure the pipeline calls <c>UseAuthentication</c> + <c>UseAuthorization</c>
    /// before this endpoint.
    /// </summary>
    public static IEndpointConventionBuilder MapMcpAgentServer(
        this IEndpointRouteBuilder builder,
        string pattern = "/mcp")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        // Delegate to the SDK's MapMcp — it handles the streamableHttp + SSE route
        // pair per the MCP 2025-06-18 spec. Our value-add is the builder + auth
        // wiring; transport plumbing stays in the SDK.
        return builder.MapMcp(pattern);
    }
}
