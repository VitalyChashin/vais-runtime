// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Vais.Agents.Observability.McpEventStore;

/// <summary>
/// Tool gateway middleware that records a <see cref="McpEvent"/> to <see cref="IMcpEventStore"/>
/// after every tool dispatch. Registration is handled by
/// <see cref="McpEventStoreExtensions.AddMcpEventStore"/>.
/// </summary>
internal sealed class McpEventMiddleware : Vais.Agents.ToolGatewayMiddleware
{
    private readonly IMcpEventStore _store;
    private readonly Vais.Agents.IAgentContextAccessor _ctx;
    private readonly string _serverId;
    private readonly ILogger<McpEventMiddleware> _logger;

    internal McpEventMiddleware(IMcpEventStore store, Vais.Agents.IAgentContextAccessor ctx, string serverId, ILogger<McpEventMiddleware> logger)
    {
        _store = store;
        _ctx = ctx;
        _serverId = serverId;
        _logger = logger;
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
            var evt = new McpEvent(
                EventId: Guid.NewGuid().ToString("N"),
                ServerId: _serverId,
                ToolName: context.ToolName,
                EventKind: eventKind,
                DurationMs: durationMs,
                CacheHit: false,
                BlockedReason: null,
                ErrorType: errorType,
                At: at,
                CorrelationId: context.AgentContext.CorrelationId,
                RunId: _ctx.Current.RunId);
            await _store.RecordAsync(evt, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record {EventKind} event for {ServerId} — best-effort, continuing", eventKind, _serverId);
        }
    }
}
