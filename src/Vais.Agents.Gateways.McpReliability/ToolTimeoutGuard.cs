// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Gateways.McpReliability;

/// <summary>
/// Gateway middleware that enforces a per-dispatch deadline. Converts timeout to a
/// <see cref="ToolCallOutcome"/> with <c>Error = "ToolTimeout"</c> rather than throwing,
/// so the model can observe the timeout and adapt its plan.
/// </summary>
public sealed class ToolTimeoutGuard(TimeSpan timeout) : ToolGatewayMiddleware
{
    /// <inheritdoc/>
    public override async Task<ToolCallOutcome> InvokeAsync(
        ToolGatewayContext context,
        Func<Task<ToolCallOutcome>> next,
        CancellationToken cancellationToken)
    {
        var nextTask = next();
        var completed = await Task.WhenAny(nextTask, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed == nextTask)
            return await nextTask.ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        return new ToolCallOutcome(
            context.CallId,
            Result: $"Tool '{context.ToolName}' timed out after {timeout.TotalSeconds:F1}s.",
            Error: "ToolTimeout");
    }
}
