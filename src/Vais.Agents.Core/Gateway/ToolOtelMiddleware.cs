// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Vais.Agents.Core;

/// <summary>
/// Gateway middleware that emits an OpenTelemetry <see cref="Activity"/> span per tool dispatch.
/// Uses <see cref="AgenticDiagnostics.ActivitySource"/>.
/// </summary>
/// <remarks>
/// If no listener is attached to the activity source the span creation is a no-op and the middleware
/// adds zero overhead. The gateway span (<c>tool.gateway/{name}</c>) is the outer span; the
/// <see cref="DefaultToolCallDispatcher"/> inner span (<c>tool.call/{name}</c>) becomes its child.
/// </remarks>
public sealed class ToolOtelMiddleware : ToolGatewayMiddleware
{
    /// <inheritdoc/>
    public override async Task<ToolCallOutcome> InvokeAsync(
        ToolGatewayContext context,
        Func<Task<ToolCallOutcome>> next,
        CancellationToken cancellationToken)
    {
        using var activity = AgenticDiagnostics.ActivitySource
            .StartActivity($"tool.gateway/{context.ToolName}");
        activity?.SetTag(AgenticTags.ToolName, context.ToolName);
        activity?.SetTag(AgenticTags.ToolCallId, context.CallId);
        activity?.SetTag(AgenticTags.WorkspaceId, context.AgentContext.WorkspaceId);
        try
        {
            var outcome = await next().ConfigureAwait(false);
            if (outcome.Error is not null)
                activity?.SetStatus(ActivityStatusCode.Error, outcome.Error);
            else
                activity?.SetStatus(ActivityStatusCode.Ok);
            return outcome;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag(AgenticTags.ErrorType, ex.GetType().Name);
            throw;
        }
    }
}

public static partial class ToolGatewayServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ToolOtelMiddleware"/> as gateway middleware. Emits an OTel
    /// <see cref="Activity"/> span per tool dispatch using
    /// <see cref="AgenticDiagnostics.ActivitySource"/>.
    /// </summary>
    public static IServiceCollection AddToolOtelMiddleware(
        this IServiceCollection services)
        => services.AddToolGatewayMiddleware<ToolOtelMiddleware>();
}
