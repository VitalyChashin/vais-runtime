// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;

namespace Vais.Agents.Gateways.McpReliability;

/// <summary>
/// Gateway middleware that implements a per-key circuit breaker over outbound tool dispatch.
/// </summary>
/// <remarks>
/// The circuit key is <see cref="AgentContext.WorkspaceId"/> (falling back to <c>"_global"</c>).
/// Full per-MCP-server-URI keying requires <c>ServerOriginUri</c> in <see cref="ToolGatewayContext"/>
/// — deferred until the Tool Registry exposes server origin (Open Question #3 in the plan).
/// </remarks>
public sealed class ToolCircuitBreakerMiddleware(
    int failureThreshold = 5,
    TimeSpan? resetTimeout = null)
    : ToolGatewayMiddleware
{
    private readonly ConcurrentDictionary<string, CircuitState> _states = new();
    private readonly TimeSpan _reset = resetTimeout ?? TimeSpan.FromSeconds(30);

    /// <inheritdoc/>
    public override async Task<ToolCallOutcome> InvokeAsync(
        ToolGatewayContext context,
        Func<Task<ToolCallOutcome>> next,
        CancellationToken cancellationToken)
    {
        var key = context.AgentContext.WorkspaceId ?? "_global";
        var state = _states.GetOrAdd(key, _ => new CircuitState());

        if (state.IsOpen(_reset))
            return new ToolCallOutcome(
                context.CallId,
                Result: $"Circuit open for '{key}' — tool call suppressed.",
                Error: "CircuitOpen");

        var outcome = await next().ConfigureAwait(false);

        if (outcome.Error is not null and not "ToolDenied" and not "CircuitOpen")
            state.RecordFailure(failureThreshold);
        else
            state.RecordSuccess();

        return outcome;
    }
}

internal sealed class CircuitState
{
    private int _failures;
    private DateTimeOffset? _openedAt;
    private readonly object _lock = new();

    public bool IsOpen(TimeSpan resetTimeout)
    {
        lock (_lock)
        {
            if (_openedAt is null) return false;
            if (DateTimeOffset.UtcNow - _openedAt > resetTimeout)
            {
                _openedAt = null;
                _failures = 0;
                return false;
            }
            return true;
        }
    }

    public void RecordFailure(int threshold)
    {
        lock (_lock)
        {
            if (++_failures >= threshold)
                _openedAt = DateTimeOffset.UtcNow;
        }
    }

    public void RecordSuccess()
    {
        lock (_lock)
        {
            _failures = 0;
            _openedAt = null;
        }
    }
}
