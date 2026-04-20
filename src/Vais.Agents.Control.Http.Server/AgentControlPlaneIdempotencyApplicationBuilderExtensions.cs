// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Vais.Agents.Control.Http;

/// <summary>
/// App-builder extension for mounting the idempotency middleware.
/// </summary>
public static class AgentControlPlaneIdempotencyApplicationBuilderExtensions
{
    /// <summary>
    /// Mount <see cref="AgentControlPlaneIdempotencyMiddleware"/> in the pipeline.
    /// Recommended position: <b>after</b> <c>UseAuthentication</c> +
    /// <c>UseAgentControlPlanePrincipal</c> (so tenant scope is populated),
    /// <b>before</b> <c>UseRouting</c>. When <see cref="IIdempotencyStore"/>
    /// isn't registered, the middleware logs a warning and passes through.
    /// </summary>
    public static IApplicationBuilder UseAgentControlPlaneIdempotency(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var store = app.ApplicationServices.GetService<IIdempotencyStore>();
        if (store is null)
        {
            // No store registered — no-op pass-through. Log once at startup.
            var loggerFactory = app.ApplicationServices.GetService<ILoggerFactory>();
            loggerFactory?
                .CreateLogger("Vais.Agents.Control.Http.Idempotency")
                .LogWarning(
                    "UseAgentControlPlaneIdempotency mounted but no IIdempotencyStore is registered. " +
                    "Call AddAgentControlPlaneIdempotency() or AddOrleansIdempotencyStore() to enable dedupe.");
            return app;
        }
        return app.UseMiddleware<AgentControlPlaneIdempotencyMiddleware>();
    }
}
