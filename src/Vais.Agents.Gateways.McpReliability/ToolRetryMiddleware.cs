// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Gateways.McpReliability;

/// <summary>
/// Gateway middleware that retries failed tool calls with exponential backoff.
/// </summary>
/// <remarks>
/// Retries on any non-null <see cref="ToolCallOutcome.Error"/>, except for
/// <c>ToolDenied</c>, <c>CircuitOpen</c>, and <c>ToolRateLimitExceeded</c> — these are
/// definitive outcomes that should not be retried.
/// </remarks>
public sealed class ToolRetryMiddleware(int maxAttempts = 3, TimeSpan? initialDelay = null)
    : ToolGatewayMiddleware
{
    private readonly TimeSpan _initial = initialDelay ?? TimeSpan.FromMilliseconds(200);

    /// <inheritdoc/>
    public override async Task<ToolCallOutcome> InvokeAsync(
        ToolGatewayContext context,
        Func<Task<ToolCallOutcome>> next,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            var outcome = await next().ConfigureAwait(false);
            if (outcome.Error is null || ++attempt >= maxAttempts)
                return outcome;
            if (outcome.Error is "ToolDenied" or "CircuitOpen" or "ToolRateLimitExceeded")
                return outcome;
            await Task.Delay(
                TimeSpan.FromMilliseconds(_initial.TotalMilliseconds * Math.Pow(2, attempt - 1)),
                cancellationToken).ConfigureAwait(false);
        }
    }
}
