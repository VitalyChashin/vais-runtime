// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Vais.Agents.Core;

/// <summary>
/// Gateway middleware that logs each tool dispatch at <see cref="LogLevel.Debug"/> level.
/// Does not mutate the outcome.
/// </summary>
public sealed class ToolLoggingMiddleware(ILogger<ToolLoggingMiddleware> logger)
    : ToolGatewayMiddleware
{
    /// <inheritdoc/>
    public override async Task<ToolCallOutcome> InvokeAsync(
        ToolGatewayContext context,
        Func<Task<ToolCallOutcome>> next,
        CancellationToken cancellationToken)
    {
        logger.LogDebug("Tool dispatch: {ToolName} (call {CallId})", context.ToolName, context.CallId);
        var outcome = await next().ConfigureAwait(false);
        if (outcome.Error is not null)
            logger.LogDebug("Tool {ToolName} error: {Error} (call {CallId})",
                context.ToolName, outcome.Error, context.CallId);
        else
            logger.LogDebug("Tool {ToolName} succeeded (call {CallId})", context.ToolName, context.CallId);
        return outcome;
    }
}

public static partial class ToolGatewayServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ToolLoggingMiddleware"/> as gateway middleware. Logs each tool
    /// dispatch at <see cref="LogLevel.Debug"/> on both success and error paths.
    /// </summary>
    public static IServiceCollection AddToolLoggingMiddleware(
        this IServiceCollection services)
        => services.AddToolGatewayMiddleware<ToolLoggingMiddleware>();
}
