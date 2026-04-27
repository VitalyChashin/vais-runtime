// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Gateways.McpSecurity;

/// <summary>
/// Gateway middleware that validates required arguments are present before dispatch.
/// Returns <c>Error = "ToolDenied"</c> when a required argument is missing.
/// </summary>
/// <remarks>
/// Only validates required-field presence. Full JSON Schema validation is a future enhancement
/// once tool metadata exposes schemas via the Tool Registry.
/// </remarks>
public sealed class ToolArgumentValidationMiddleware(
    IReadOnlyDictionary<string, IReadOnlyList<string>> requiredArgsByTool)
    : ToolGatewayMiddleware
{
    /// <inheritdoc/>
    public override Task<ToolCallOutcome> InvokeAsync(
        ToolGatewayContext context,
        Func<Task<ToolCallOutcome>> next,
        CancellationToken cancellationToken)
    {
        if (requiredArgsByTool.TryGetValue(context.ToolName, out var required))
        {
            foreach (var field in required)
            {
                if (!context.Arguments.TryGetProperty(field, out _))
                    return Task.FromResult(new ToolCallOutcome(
                        context.CallId,
                        Result: $"Required argument '{field}' missing for tool '{context.ToolName}'.",
                        Error: "ToolDenied"));
            }
        }
        return next();
    }
}
