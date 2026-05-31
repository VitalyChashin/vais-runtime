// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Vais.Agents.Observability.RunHealthStore;

/// <summary>
/// Hosted service that subscribes to <see cref="IAgentEventBus"/> and persists each
/// <em>mechanical-failure</em> agent event to <see cref="IRunHealthStore"/>. This is the write
/// path the Part 1 severity signals never had: before this subscriber, a recovered tool error,
/// LLM retry, or provider fallback was published to the bus and then dropped, so a run that
/// "completed" carried no durable trace of having degraded.
/// </summary>
/// <remarks>
/// Like <c>RunStoreSubscriber</c>, this runs on <b>every</b> silo, so on a cross-silo stream
/// provider each event reaches the store once per silo (ADR 019, at-least-once). The store's
/// composite-PK <c>ON CONFLICT DO NOTHING</c> makes duplicate delivery idempotent.
/// </remarks>
internal sealed class RunHealthSignalSubscriber : IHostedService
{
    private readonly IRunHealthStore _store;
    private readonly IAgentEventBus _bus;
    private readonly RunHealthStoreOptions _options;
    private readonly ILogger<RunHealthSignalSubscriber> _logger;
    private readonly IFailureOntologyCatalog? _catalog;
    private IDisposable? _subscription;

    public RunHealthSignalSubscriber(
        IRunHealthStore store,
        IAgentEventBus bus,
        IOptions<RunHealthStoreOptions> options,
        ILogger<RunHealthSignalSubscriber> logger,
        IFailureOntologyCatalog? catalog = null)
    {
        _store = store;
        _bus = bus;
        _options = options.Value;
        _logger = logger;
        _catalog = catalog;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(_options.RetentionDays);
        await _store.DeleteOlderThanAsync(cutoff, cancellationToken).ConfigureAwait(false);

        _subscription = _bus.Subscribe(HandleAsync);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }

    private async ValueTask HandleAsync(AgentEvent evt, CancellationToken ct)
    {
        var record = Map(evt);
        if (record is null)
        {
            return;
        }

        if (_catalog is not null)
            record = record with { ConceptName = _catalog.FromSignalKind(record.Kind)?.Name };

        try
        {
            await _store.RecordSignalAsync(record, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RunHealthStore failed to persist {Kind} signal for run {RunId}.",
                record.Kind, record.RunId);
        }
    }

    /// <summary>
    /// Projects a bus event onto a <see cref="RunHealthSignalRecord"/>, or <see langword="null"/>
    /// for events that are not mechanical-failure signals (or that lack a run id to roll up to).
    /// MCP and graph-node failures are sourced from the gateway/run stores, not the bus, so they
    /// are not mapped here.
    /// </summary>
    internal static RunHealthSignalRecord? Map(AgentEvent evt)
    {
        var runId = evt.Context.RunId;
        if (string.IsNullOrEmpty(runId))
        {
            return null;
        }

        var correlationId = evt.Context.CorrelationId;
        var agent = evt.Context.AgentName ?? string.Empty;

        return evt switch
        {
            ToolCallCompleted { Succeeded: false } e =>
                new RunHealthSignalRecord(runId, correlationId, e.ToolName,
                    RunHealthSignalKind.ToolError, e.Level, e.Error, IsTransient: false, e.At),

            TurnFailed e =>
                new RunHealthSignalRecord(runId, correlationId, agent,
                    RunHealthSignalKind.TurnFailed, FailureLevel.Error, e.ErrorType, IsTransient: false, e.At),

            TurnCompleted { Level: not FailureLevel.Default } e =>
                new RunHealthSignalRecord(runId, correlationId, agent,
                    RunHealthSignalKind.TurnPartial, e.Level, ErrorType: null, IsTransient: false, e.At),

            LlmCallRetried e =>
                new RunHealthSignalRecord(runId, correlationId, agent,
                    RunHealthSignalKind.LlmRetry, e.Level, e.ErrorType, e.IsTransient, e.At),

            LlmFallbackEngaged e =>
                new RunHealthSignalRecord(runId, correlationId, agent,
                    RunHealthSignalKind.LlmFallback, e.Level, e.Reason, IsTransient: false, e.At),

            GuardrailTriggered e =>
                new RunHealthSignalRecord(runId, correlationId, agent,
                    RunHealthSignalKind.Guardrail, FailureLevel.Warning, e.Reason, IsTransient: false, e.At),

            _ => null,
        };
    }
}
