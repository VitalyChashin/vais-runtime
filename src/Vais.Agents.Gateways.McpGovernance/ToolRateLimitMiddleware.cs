// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Vais.Agents.Gateways.Governance;

namespace Vais.Agents.Gateways.McpGovernance;

/// <summary>
/// Gateway middleware that rate-limits tool calls per workspace and tool name using
/// <see cref="IRateLimitStore"/> from <c>Vais.Agents.Gateways.Governance</c>.
/// Returns <c>Error = "ToolRateLimitExceeded"</c> when the limit is hit.
/// </summary>
public sealed class ToolRateLimitMiddleware(
    IRateLimitStore store,
    ToolRateLimitOptions options)
    : ToolGatewayMiddleware
{
    /// <inheritdoc/>
    public override async Task<ToolCallOutcome> InvokeAsync(
        ToolGatewayContext context,
        Func<Task<ToolCallOutcome>> next,
        CancellationToken cancellationToken)
    {
        var key = $"tool:{context.AgentContext.WorkspaceId ?? "_global"}:{context.ToolName}";
        var (requests, _) = await store.RecordAndGetAsync(
            key, tokens: 1, options.Window, cancellationToken).ConfigureAwait(false);

        if (requests > options.MaxRequestsPerWindow)
            return new ToolCallOutcome(
                context.CallId,
                Result: $"Tool '{context.ToolName}' rate limit exceeded.",
                Error: "ToolRateLimitExceeded");

        return await next().ConfigureAwait(false);
    }
}
