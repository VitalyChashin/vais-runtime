// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Hosted service that bridges <see cref="IAgentEventBus"/> (<see cref="TurnCompleted"/>) and
/// <see cref="IAgentGraphEventBus"/> (<see cref="GraphCompleted"/>) to every registered
/// <see cref="IRunCompletionListener"/>. One silo-wide subscription per bus;
/// listeners receive every event and apply their own filtering.
/// </summary>
internal sealed class RunCompletionEventBusBridge : IHostedService, IDisposable
{
    private readonly IAgentEventBus _bus;
    private readonly IAgentGraphEventBus? _graphBus;
    private readonly IServiceProvider _services;
    private readonly ILogger<RunCompletionEventBusBridge> _logger;
    private IDisposable? _subscription;
    private IDisposable? _graphSubscription;

    public RunCompletionEventBusBridge(
        IAgentEventBus bus,
        IServiceProvider services,
        ILogger<RunCompletionEventBusBridge> logger,
        IAgentGraphEventBus? graphBus = null)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);
        _bus = bus;
        _services = services;
        _logger = logger;
        _graphBus = graphBus;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _bus.Subscribe(HandleTurnEventAsync);
        if (_graphBus is not null)
            _graphSubscription = _graphBus.Subscribe(HandleGraphEventAsync);
        _logger.LogDebug("RunCompletionEventBusBridge started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        _graphSubscription?.Dispose();
        _graphSubscription = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _graphSubscription?.Dispose();
    }

    private async ValueTask HandleTurnEventAsync(AgentEvent evt, CancellationToken ct)
    {
        if (evt is not TurnCompleted tc) return;
        var signal = new RunCompletionSignal(
            AgentRunId: tc.Context.RunId ?? tc.Context.CorrelationId ?? Guid.NewGuid().ToString(),
            AgentRef: tc.Context.AgentName,
            GraphRef: null,
            WorkspaceId: tc.Context.WorkspaceId ?? string.Empty,
            CompletedAt: tc.At,
            Duration: tc.Duration,
            AssistantText: tc.AssistantText,
            FinalState: null);
        await FanOutAsync(signal, ct);
    }

    private async ValueTask HandleGraphEventAsync(AgentGraphEvent evt, CancellationToken ct)
    {
        if (evt is not GraphCompleted gc) return;
        var signal = new RunCompletionSignal(
            AgentRunId: gc.Context.RunId ?? gc.RunId,
            AgentRef: null,
            GraphRef: gc.Context.AgentName,
            WorkspaceId: gc.Context.WorkspaceId ?? string.Empty,
            CompletedAt: gc.At,
            Duration: gc.Duration,
            AssistantText: null,
            FinalState: gc.FinalState);
        await FanOutAsync(signal, ct);
    }

    private async ValueTask FanOutAsync(RunCompletionSignal signal, CancellationToken ct)
    {
        var listeners = _services.GetServices<IRunCompletionListener>();
        foreach (var listener in listeners)
        {
            try
            {
                await listener.OnRunCompletedAsync(signal, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "IRunCompletionListener {Type} threw on run {RunId}",
                    listener.GetType().Name, signal.AgentRunId);
            }
        }
    }
}
