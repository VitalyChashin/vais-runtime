// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Gateways.McpTransformation;

/// <summary>
/// Gateway middleware that attempts to repair malformed JSON in tool responses.
/// If the response is already valid JSON, or if repair fails, the outcome is returned unchanged.
/// </summary>
/// <remarks>
/// Current repair scope: validates JSON; stub repair method returns null (no-op).
/// Full heuristic repair (trailing-comma removal, brace balancing) is a follow-up enhancement.
/// </remarks>
public sealed class ToolJsonRepairMiddleware : ToolGatewayMiddleware
{
    /// <inheritdoc/>
    public override async Task<ToolCallOutcome> InvokeAsync(
        ToolGatewayContext context,
        Func<Task<ToolCallOutcome>> next,
        CancellationToken cancellationToken)
    {
        var outcome = await next().ConfigureAwait(false);
        if (outcome.Error is not null) return outcome;
        try
        {
            JsonDocument.Parse(outcome.Result);
            return outcome;
        }
        catch (JsonException)
        {
            var repaired = AttemptRepair(outcome.Result);
            return repaired is not null ? outcome with { Result = repaired } : outcome;
        }
    }

    private static string? AttemptRepair(string json) => null;
}
