// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using A2A;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;

namespace Vais.Agents.Protocols.A2A.Server;

/// <summary>
/// JWT auth wiring for the A2A inbound server. Mirrors the v0.7 MCP server's shape
/// — same dual-header logic, same consumer-facing API — but registers the bearer
/// scheme under <see cref="A2ABearerSchemeName"/> so consumers running both
/// protocols side-by-side can see independent audit trails.
/// </summary>
public static class A2AAgentServerJwtAuthExtensions
{
    /// <summary>Scheme name the A2A bearer is registered under. Distinct from MCP's default so audit logs can tell them apart.</summary>
    public const string A2ABearerSchemeName = "A2AJwt";

    /// <summary>
    /// Register JWT bearer auth for the A2A endpoints under a dedicated scheme
    /// (<see cref="A2ABearerSchemeName"/>). Accepts tokens from either
    /// <c>Authorization: Bearer …</c> (direct-call) or
    /// <c>X-Upstream-Authorization: Bearer …</c> (gateway-forwarded — ContextForge
    /// and similar hubs). Upstream header wins when both are present.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Side-effect: reaches into <see cref="A2AAgentServerOptions"/>'s
    /// <see cref="A2AAgentServerOptions.CustomizeCard"/> hook and <em>prepends</em> a
    /// card-customizer that populates <c>AgentCard.SecuritySchemes["bearer"]</c> with
    /// an <see cref="HttpAuthSecurityScheme"/> describing the JWT shape, so discovery
    /// clients (<c>.well-known/agent-card.json</c>) know what to send. Any consumer
    /// customizer still runs afterwards and can override the scheme.
    /// </para>
    /// <para>
    /// Callers must have registered options via <c>AddA2AAgentServer</c> before calling
    /// this method — we read the options singleton out of the DI container to wire the
    /// customizer. This matches the MCP server's ordering convention.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddA2AAgentServerJwtAuth(
        this IServiceCollection services,
        Action<JwtBearerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddAuthentication(A2ABearerSchemeName)
            .AddJwtBearer(A2ABearerSchemeName, opts =>
            {
                configure(opts);
                var prior = opts.Events?.OnMessageReceived;
                opts.Events ??= new JwtBearerEvents();
                opts.Events.OnMessageReceived = async ctx =>
                {
                    if (prior is not null) await prior(ctx).ConfigureAwait(false);
                    // Upstream header trumps Authorization when present — matches the MCP
                    // server's dual-header convention.
                    if (ctx.Request.Headers.TryGetValue("X-Upstream-Authorization", out var upstream) &&
                        upstream.Count > 0 &&
                        upstream[0] is { } raw &&
                        raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    {
                        ctx.Token = raw["Bearer ".Length..];
                    }
                };
            });
        services.AddAuthorization();

        // Chain a card customizer that advertises the bearer scheme on every derived card.
        // Doing this at DI-wire time means the options singleton is mutated once and picked
        // up by MapA2AAgentServer when it enumerates entries at startup.
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(A2AAgentServerOptions));
        if (descriptor is { ImplementationInstance: A2AAgentServerOptions opts })
        {
            var priorCardHook = opts.CustomizeCard;
            opts.CustomizeCard = (manifest, card) =>
            {
                AdvertiseBearer(card);
                priorCardHook?.Invoke(manifest, card);
            };
        }
        else
        {
            // Options weren't registered as a concrete instance — defer via a postconfigure
            // so it still takes effect if the consumer calls AddA2AAgentServer after this.
            services.AddOptions<A2AAgentServerOptions>().PostConfigure(o =>
            {
                var priorCardHook = o.CustomizeCard;
                o.CustomizeCard = (manifest, card) =>
                {
                    AdvertiseBearer(card);
                    priorCardHook?.Invoke(manifest, card);
                };
            });
        }

        return services;
    }

    internal static void AdvertiseBearer(AgentCard card)
    {
        card.SecuritySchemes ??= new Dictionary<string, SecurityScheme>(StringComparer.Ordinal);
        if (card.SecuritySchemes.ContainsKey("bearer"))
        {
            // Don't overwrite a caller-supplied scheme on the same key.
            return;
        }
        card.SecuritySchemes["bearer"] = new SecurityScheme
        {
            HttpAuthSecurityScheme = new HttpAuthSecurityScheme
            {
                Scheme = "bearer",
                BearerFormat = "JWT",
            },
        };
    }
}
