// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Vais.Agents.Observability.McpGatewayEventStore;

/// <summary>
/// Tool gateway middleware that records a <see cref="McpGatewayEvent"/> to <see cref="IMcpGatewayEventStore"/>
/// after every tool dispatch. Registration is handled by
/// <see cref="McpGatewayEventStoreExtensions.AddMcpGatewayEventStore"/>.
/// </summary>
internal sealed class McpGatewayEventMiddleware : Vais.Agents.ToolGatewayMiddleware
{
    private readonly IMcpGatewayEventStore _store;
    private readonly Vais.Agents.IAgentContextAccessor _ctx;
    private readonly string _gatewayId;
    private readonly ILogger<McpGatewayEventMiddleware> _logger;

    internal McpGatewayEventMiddleware(IMcpGatewayEventStore store, Vais.Agents.IAgentContextAccessor ctx, string gatewayId, ILogger<McpGatewayEventMiddleware> logger)
    {
        _store = store;
        _ctx = ctx;
        _gatewayId = gatewayId;
        _logger = logger;
    }

    public override async Task<Vais.Agents.ToolCallOutcome> InvokeAsync(
        Vais.Agents.ToolGatewayContext context,
        Func<Task<Vais.Agents.ToolCallOutcome>> next,
        CancellationToken cancellationToken = default)
    {
        var at = DateTimeOffset.UtcNow;
        var inputJson = Truncate(context.Arguments.GetRawText());
        var sw = Stopwatch.StartNew();
        try
        {
            var outcome = await next().ConfigureAwait(false);
            sw.Stop();
            var outputJson = Truncate(outcome.Error ?? outcome.Result);
            _ = TryRecordAsync(at, sw.ElapsedMilliseconds, context,
                outcome.Error is null ? "call.completed" : "call.failed",
                errorType: outcome.Error, inputJson: inputJson, outputJson: outputJson);
            return outcome;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            _ = TryRecordAsync(at, sw.ElapsedMilliseconds, context,
                "call.failed", errorType: ex.GetType().Name, inputJson: inputJson, outputJson: null);
            throw;
        }
    }

    private async Task TryRecordAsync(DateTimeOffset at, long durationMs,
        Vais.Agents.ToolGatewayContext context, string eventKind, string? errorType,
        string? inputJson, string? outputJson)
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
                RunId: _ctx.Current.RunId,
                InputJson: inputJson,
                OutputJson: outputJson);
            await _store.RecordAsync(evt, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record {EventKind} event for {GatewayId} — best-effort, continuing", eventKind, _gatewayId);
        }
    }

    private static string? Truncate(string? s, int max = 32 * 1024) =>
        s is null || s.Length <= max ? s : s[..max] + "[truncated]";
}
