// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Gateways.McpGovernance;

/// <summary>
/// Gateway middleware that enforces config-driven workspace tool policies. Denies tool calls
/// whose names match the workspace's deny prefix list or fail the privilege level check.
/// </summary>
/// <remarks>
/// OPA/Cedar enforcement is proprietary. This is the OSS config-driven reference implementation.
/// </remarks>
public sealed class ToolWorkspacePolicyMiddleware(
    IReadOnlyDictionary<string, WorkspaceToolPolicy> policies)
    : ToolGatewayMiddleware
{
    /// <inheritdoc/>
    public override Task<ToolCallOutcome> InvokeAsync(
        ToolGatewayContext context,
        Func<Task<ToolCallOutcome>> next,
        CancellationToken cancellationToken)
    {
        var wsId = context.AgentContext.WorkspaceId;
        if (wsId is not null
            && policies.TryGetValue(wsId, out var policy)
            && !policy.IsAllowed(context.ToolName, context.AgentContext.PrivilegeLevel))
        {
            return Task.FromResult(new ToolCallOutcome(
                context.CallId,
                Result: $"Tool '{context.ToolName}' is not permitted in workspace '{wsId}'.",
                Error: "ToolDenied"));
        }
        return next();
    }
}
