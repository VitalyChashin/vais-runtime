// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;

namespace Vais.Agents.Core;

/// <summary>
/// Gateway middleware that short-circuits tool calls whose names match a static block list.
/// Returns a <see cref="ToolCallOutcome"/> with <c>Error = "ToolDenied"</c> so the model can
/// observe the denial and adapt its plan.
/// </summary>
/// <remarks>
/// Operates on exact name matching (case-insensitive). Glob/regex patterns are in
/// <c>Vais.Agents.Gateways.McpSecurity</c> (Phase 3). This is complementary to
/// <see cref="AgentContext.AllowedTools"/> RCB enforcement in <c>DefaultToolCallDispatcher</c>:
/// the RCB allow-list is dynamic per-context; the deny filter is static config.
/// </remarks>
public sealed class ToolDenyFilterMiddleware(IReadOnlyList<string> blockedToolNames)
    : ToolGatewayMiddleware
{
    /// <inheritdoc/>
    public override Task<ToolCallOutcome> InvokeAsync(
        ToolGatewayContext context,
        Func<Task<ToolCallOutcome>> next,
        CancellationToken cancellationToken)
    {
        foreach (var blocked in blockedToolNames)
        {
            if (string.Equals(context.ToolName, blocked, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new ToolCallOutcome(
                    context.CallId,
                    Result: $"Tool '{context.ToolName}' is blocked by the gateway deny filter.",
                    Error: "ToolDenied"));
        }
        return next();
    }
}

public static partial class ToolGatewayServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ToolDenyFilterMiddleware"/> as a singleton gateway middleware with
    /// the specified <paramref name="blockedToolNames"/> block list.
    /// </summary>
    public static IServiceCollection AddToolDenyFilterMiddleware(
        this IServiceCollection services,
        IReadOnlyList<string> blockedToolNames)
    {
        services.AddSingleton<ToolGatewayMiddleware>(
            new ToolDenyFilterMiddleware(blockedToolNames));
        return services;
    }
}
