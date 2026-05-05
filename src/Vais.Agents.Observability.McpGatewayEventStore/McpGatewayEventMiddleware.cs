// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;

namespace Vais.Agents.Observability.McpGatewayEventStore;

/// <summary>
/// Tool gateway middleware that records a <see cref="McpGatewayEvent"/> to <see cref="IMcpGatewayEventStore"/>
/// after every tool dispatch. Registration is handled by
/// <see cref="McpGatewayEventStoreExtensions.AddMcpGatewayEventStore"/>.
/// </summary>
internal sealed class McpGatewayEventMiddleware : Vais.Agents.ToolGatewayMiddleware
{
    private readonly IMcpGatewayEventStore _store;
    private readonly string _gatewayId;

    internal McpGatewayEventMiddleware(IMcpGatewayEventStore store, string gatewayId)
    {
        _store = store;
        _gatewayId = gatewayId;
    }

    public override async Task<Vais.Agents.ToolCallOutcome> InvokeAsync(
        Vais.Agents.ToolGatewayContext context,
        Func<Task<Vais.Agents.ToolCallOutcome>> next,
        CancellationToken cancellationToken = default)
    {
        var at = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        try
        {
            var outcome = await next().ConfigureAwait(false);
            sw.Stop();
            _ = TryRecordAsync(at, sw.ElapsedMilliseconds, context,
                outcome.Error is null ? "call.completed" : "call.failed",
                errorType: outcome.Error);
            return outcome;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            _ = TryRecordAsync(at, sw.ElapsedMilliseconds, context,
                "call.failed", errorType: ex.GetType().Name);
            throw;
        }
    }

    private async Task TryRecordAsync(DateTimeOffset at, long durationMs,
        Vais.Agents.ToolGatewayContext context, string eventKind, string? errorType)
    {
        try
        {
            var evt = new McpGatewayEvent(
                EventId: Guid.NewGuid().ToString("N"),
                GatewayId: _gatewayId,
                ToolName: context.ToolName,
                EventKind: eventKind,
                DurationMs: durationMs,
                CacheHit: false,
                BlockedReason: null,
                ErrorType: errorType,
                At: at,
                CorrelationId: context.AgentContext.CorrelationId,
                RunId: null);
            await _store.RecordAsync(evt, CancellationToken.None).ConfigureAwait(false);
        }
        catch { /* best-effort */ }
    }
}
