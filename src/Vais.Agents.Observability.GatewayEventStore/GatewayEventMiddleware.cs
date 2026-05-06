// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Vais.Agents.Observability.GatewayEventStore;

/// <summary>
/// LLM gateway middleware that records a <see cref="GatewayEvent"/> to <see cref="IGatewayEventStore"/>
/// after every completion (both sync and streaming). Registration is handled by
/// <see cref="GatewayEventStoreExtensions.AddGatewayEventStore"/>.
/// </summary>
internal sealed class GatewayEventMiddleware : Vais.Agents.LlmGatewayMiddleware
{
    private readonly IGatewayEventStore _store;
    private readonly Vais.Agents.IAgentContextAccessor _ctx;
    private readonly string _gatewayId;
    private readonly ILogger<GatewayEventMiddleware> _logger;

    internal GatewayEventMiddleware(
        IGatewayEventStore store,
        Vais.Agents.IAgentContextAccessor ctx,
        string gatewayId,
        ILogger<GatewayEventMiddleware> logger)
    {
        _store = store;
        _ctx = ctx;
        _gatewayId = gatewayId;
        _logger = logger;
    }

    protected override async Task<Vais.Agents.CompletionResponse> InvokeAsync(
        Vais.Agents.CompletionRequest request,
        Func<Vais.Agents.CompletionRequest, CancellationToken, Task<Vais.Agents.CompletionResponse>> next,
        CancellationToken cancellationToken)
    {
        var at = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await next(request, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            _ = TryRecordAsync(at, sw.ElapsedMilliseconds, response.ModelId,
                response.PromptTokens ?? 0, response.CompletionTokens ?? 0,
                "completion.completed", null);
            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            _ = TryRecordAsync(at, sw.ElapsedMilliseconds, null, 0, 0,
                "completion.failed", ex.GetType().Name);
            throw;
        }
    }

    protected override IAsyncEnumerable<Vais.Agents.CompletionUpdate> InvokeStreamAsync(
        Vais.Agents.CompletionRequest request,
        Func<Vais.Agents.CompletionRequest, CancellationToken, IAsyncEnumerable<Vais.Agents.CompletionUpdate>> next,
        CancellationToken cancellationToken)
        => StreamAndRecordAsync(next(request, cancellationToken), cancellationToken);

    private async IAsyncEnumerable<Vais.Agents.CompletionUpdate> StreamAndRecordAsync(
        IAsyncEnumerable<Vais.Agents.CompletionUpdate> source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var at = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        string? modelId = null;
        int promptTokens = 0, completionTokens = 0;
        await foreach (var update in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            modelId ??= update.ModelId;
            promptTokens = update.PromptTokens ?? promptTokens;
            completionTokens = update.CompletionTokens ?? completionTokens;
            yield return update;
        }
        sw.Stop();
        _ = TryRecordAsync(at, sw.ElapsedMilliseconds, modelId, promptTokens, completionTokens,
            "completion.completed", null);
    }

    private async Task TryRecordAsync(DateTimeOffset at, long durationMs,
        string? modelId, int inputTokens, int outputTokens,
        string eventKind, string? errorType)
    {
        try
        {
            var evt = new GatewayEvent(
                EventId: Guid.NewGuid().ToString("N"),
                GatewayId: _gatewayId,
                EventKind: eventKind,
                ModelId: modelId,
                InputTokens: inputTokens,
                OutputTokens: outputTokens,
                DurationMs: durationMs,
                CacheHit: null,
                ErrorType: errorType,
                At: at,
                CorrelationId: _ctx.Current.CorrelationId,
                RunId: null);
            await _store.RecordAsync(evt, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record {EventKind} event for {GatewayId} — best-effort, continuing", eventKind, _gatewayId);
        }
    }
}
