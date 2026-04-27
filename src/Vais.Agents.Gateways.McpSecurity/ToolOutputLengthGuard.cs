// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Gateways.McpSecurity;

/// <summary>
/// Gateway middleware that rejects (not truncates) tool responses exceeding a configured size.
/// Returns <c>Error = "ToolOutputTooLarge"</c> for oversized responses.
/// </summary>
/// <remarks>
/// Distinction from <c>ToolResponseTruncationMiddleware</c>: truncation silently clips;
/// this guard explicitly rejects. Security-conscious deployments use the guard;
/// latency-sensitive deployments use truncation.
/// </remarks>
public sealed class ToolOutputLengthGuard(int maxCharacters) : ToolGatewayMiddleware
{
    /// <inheritdoc/>
    public override async Task<ToolCallOutcome> InvokeAsync(
        ToolGatewayContext context,
        Func<Task<ToolCallOutcome>> next,
        CancellationToken cancellationToken)
    {
        var outcome = await next().ConfigureAwait(false);
        if (outcome.Error is null && outcome.Result.Length > maxCharacters)
            return new ToolCallOutcome(
                context.CallId,
                Result: $"Tool '{context.ToolName}' response exceeded {maxCharacters} characters and was rejected.",
                Error: "ToolOutputTooLarge");
        return outcome;
    }
}
