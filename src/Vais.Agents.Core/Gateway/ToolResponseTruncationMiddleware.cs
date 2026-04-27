// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;

namespace Vais.Agents.Core;

/// <summary>
/// Gateway middleware that truncates oversized tool responses before they re-enter the agent's
/// context window. Addresses context window pollution from large tool outputs.
/// </summary>
/// <remarks>
/// Truncation is by character count, not tokens, to avoid a tokenizer dependency. For token-aware
/// truncation, use <c>ToolTokenBudgetMiddleware</c> in <c>Vais.Agents.Gateways.McpTransformation</c>.
/// Error outcomes are never truncated — error messages must be fully visible to the model.
/// </remarks>
public sealed class ToolResponseTruncationMiddleware(int maxCharacters = 4096)
    : ToolGatewayMiddleware
{
    /// <inheritdoc/>
    public override async Task<ToolCallOutcome> InvokeAsync(
        ToolGatewayContext context,
        Func<Task<ToolCallOutcome>> next,
        CancellationToken cancellationToken)
    {
        var outcome = await next().ConfigureAwait(false);

        if (outcome.Error is not null || outcome.Result.Length <= maxCharacters)
            return outcome;

        var truncated = string.Concat(
            outcome.Result.AsSpan(0, maxCharacters),
            $"\n[Truncated: response exceeded {maxCharacters} characters]");
        return outcome with { Result = truncated };
    }
}

public static partial class ToolGatewayServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ToolResponseTruncationMiddleware"/> as a singleton gateway middleware.
    /// </summary>
    public static IServiceCollection AddToolResponseTruncationMiddleware(
        this IServiceCollection services,
        int maxCharacters = 4096)
    {
        services.AddSingleton<ToolGatewayMiddleware>(
            new ToolResponseTruncationMiddleware(maxCharacters));
        return services;
    }
}
