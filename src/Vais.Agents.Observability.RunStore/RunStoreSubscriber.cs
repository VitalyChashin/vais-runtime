// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Vais.Agents.Observability.RunStore;

internal sealed class RunStoreSubscriber : IHostedService
{
    private readonly IRunStore _store;
    private readonly IAgentGraphEventBus _bus;
    private readonly RunStoreOptions _options;
    private readonly ILogger<RunStoreSubscriber> _logger;
    private IDisposable? _subscription;

    public RunStoreSubscriber(
        IRunStore store,
        IAgentGraphEventBus bus,
        IOptions<RunStoreOptions> options,
        ILogger<RunStoreSubscriber> logger)
    {
        _store = store;
        _bus = bus;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(_options.RetentionDays);
        await _store.DeleteRunsOlderThanAsync(cutoff, cancellationToken).ConfigureAwait(false);

        _subscription = _bus.Subscribe(HandleAsync);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }

    private ValueTask HandleAsync(AgentGraphEvent evt, CancellationToken ct)
    {
        var task = evt switch
        {
            GraphStarted e => _store.StartRunAsync(e.RunId, e.GraphId, ct),
            NodeStarted e => _store.StartNodeAsync(e.RunId, e.NodeId, e.NodeKind, null, ct),
            NodeAgentInvoked e => _store.RecordNodeInvocationAsync(e.RunId, e.NodeId, e.AgentId,
                e.InputText, e.OutputText, e.InputTokens, e.OutputTokens, ct),
            NodeCompleted e => _store.CompleteNodeAsync(e.RunId, e.NodeId, ct),
            EdgeTraversed e => _store.RecordEdgeAsync(e.RunId, e.From, e.To, ct),
            GraphInterrupted e => _store.InterruptRunAsync(e.RunId, e.InterruptId, ct),
            GraphCompleted e => _store.CompleteRunAsync(e.RunId, e.SuperStep, ct),
            GraphFailed e => _store.FailRunAsync(e.RunId, e.ErrorMessage, ct),
            _ => Task.CompletedTask,
        };

        return new ValueTask(task.ContinueWith(t =>
        {
            if (t.IsFaulted)
                _logger.LogWarning(t.Exception, "RunStore failed to handle {EventType}.", evt.GetType().Name);
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default));
    }
}
