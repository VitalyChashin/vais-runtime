// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Gateways.McpCache;

/// <summary>
/// Gateway middleware that short-circuits repeated tool calls with identical arguments by
/// returning cached outcomes from <see cref="IToolResultCache"/>.
/// </summary>
/// <remarks>
/// Only successful outcomes are stored — transient errors must not permanently poison the cache.
/// Non-deterministic tools (e.g. <c>get_current_time</c>) should be listed in
/// <paramref name="excludedTools"/> to bypass caching.
/// </remarks>
public sealed class ToolResultCacheMiddleware(
    IToolResultCache cache,
    IReadOnlyList<string>? excludedTools = null)
    : ToolGatewayMiddleware
{
    private readonly HashSet<string> _excluded = excludedTools is null
        ? []
        : new HashSet<string>(excludedTools, StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public override async Task<ToolCallOutcome> InvokeAsync(
        ToolGatewayContext context,
        Func<Task<ToolCallOutcome>> next,
        CancellationToken cancellationToken)
    {
        if (_excluded.Contains(context.ToolName))
            return await next().ConfigureAwait(false);

        var hit = await cache.TryGetAsync(
            context.ToolName, context.Arguments, cancellationToken).ConfigureAwait(false);
        if (hit is not null)
            return hit with { CallId = context.CallId };

        var outcome = await next().ConfigureAwait(false);

        if (outcome.Error is null)
            await cache.SetAsync(
                context.ToolName, context.Arguments, outcome, cancellationToken).ConfigureAwait(false);

        return outcome;
    }
}
