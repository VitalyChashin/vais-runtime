// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vais.Agents.Core;

namespace Vais.Agents.Control.Http;

/// <summary>
/// DI convenience for wiring JWT bearer authentication on top of
/// <see cref="AgentControlPlaneServiceCollectionExtensions.AddAgentControlPlane"/>.
/// Consumers who need a different auth scheme (mTLS, API-key, cookies) register
/// their own <c>AddAuthentication</c> + <see cref="IPrincipalMapper"/> and skip
/// this extension.
/// </summary>
public static class AgentControlPlaneAuthServiceCollectionExtensions
{
    /// <summary>
    /// Register JWT bearer validation with <paramref name="configure"/> + the default
    /// <see cref="IPrincipalMapper"/>. Also installs an <see cref="AsyncLocalAgentContextAccessor"/>
    /// + middleware that pushes the mapped principal onto it for the request scope.
    /// </summary>
    public static IServiceCollection AddAgentControlPlaneJwtAuth(
        this IServiceCollection services,
        Action<JwtBearerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(configure);
        services.AddAuthorization();

        services.TryAddSingleton<IPrincipalMapper, DefaultPrincipalMapper>();
        services.TryAddSingleton<AsyncLocalAgentContextAccessor>();
        services.TryAddSingleton<IAgentContextAccessor>(sp => sp.GetRequiredService<AsyncLocalAgentContextAccessor>());
        services.TryAddSingleton<IAgentContextSetter>(sp => sp.GetRequiredService<AsyncLocalAgentContextAccessor>());
        return services;
    }
}

/// <summary>
/// Middleware hooks for wiring the HTTP principal into the ambient agent context.
/// Call <see cref="UseAgentControlPlanePrincipalMapping"/> in the pipeline
/// <em>after</em> <c>UseAuthentication</c> + <c>UseAuthorization</c> and
/// <em>before</em> <c>MapAgentControlPlane</c>.
/// </summary>
public static class AgentControlPlanePrincipalMiddlewareExtensions
{
    /// <summary>
    /// Map the incoming <c>HttpContext.User</c> to an <see cref="AgentPrincipal"/>
    /// via the registered <see cref="IPrincipalMapper"/> and push it onto the
    /// <see cref="AsyncLocalAgentContextAccessor"/> for the duration of the request.
    /// The lifecycle manager reads the principal from its <see cref="IAgentContextAccessor"/>
    /// on every verb, so policy engines see it automatically.
    /// </summary>
    public static IApplicationBuilder UseAgentControlPlanePrincipalMapping(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Use(async (ctx, next) =>
        {
            var mapper = ctx.RequestServices.GetService<IPrincipalMapper>();
            var accessor = ctx.RequestServices.GetService<AsyncLocalAgentContextAccessor>();
            if (mapper is null || accessor is null)
            {
                await next(ctx).ConfigureAwait(false);
                return;
            }
            var principal = ctx.User is not null ? mapper.Map(ctx.User) : null;
            var current = accessor.Current;
            var overlayed = principal is null
                ? current
                : current with { UserId = principal.Id, TenantId = principal.TenantId, Scopes = principal.Scopes };
            using (accessor.Push(overlayed))
            {
                await next(ctx).ConfigureAwait(false);
            }
        });
        return app;
    }
}
