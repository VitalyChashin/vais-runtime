// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Default <see cref="IBackgroundAgentRunGrain"/> implementation.
/// Each grain activation owns one background sub-run, identified by the child session id.
/// </summary>
public sealed class BackgroundAgentRunGrain : Grain, IBackgroundAgentRunGrain
{
    private readonly IPersistentState<BackgroundAgentRunGrainState> _state;
    private CancellationTokenSource? _cts;

    /// <summary>Grain constructor. Dependencies resolved from silo DI.</summary>
    public BackgroundAgentRunGrain(
        [PersistentState("bg-run", AiAgentGrain.StorageName)] IPersistentState<BackgroundAgentRunGrainState> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    /// <inheritdoc />
    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await base.OnActivateAsync(cancellationToken);

        // On silo restart: if the run was interrupted mid-execution, re-schedule it.
        var status = _state.State.Status;
        if (status is BackgroundAgentRunStatus.Pending or BackgroundAgentRunStatus.Running
            && !string.IsNullOrEmpty(_state.State.Handle))
        {
            var self = GrainFactory.GetGrain<IBackgroundAgentRunGrain>(this.GetPrimaryKeyString());
            _ = self.RunAsync();
        }
    }

    /// <inheritdoc />
    public async Task<string> StartAsync(
        string parentRunId,
        string childAgentId,
        string message,
        AgentContext childContext)
    {
        var handle = this.GetPrimaryKeyString();
        _state.State = new BackgroundAgentRunGrainState
        {
            Handle       = handle,
            ParentRunId  = parentRunId,
            ChildAgentId = childAgentId,
            Message      = message,
            ChildContext  = childContext,
            Status       = BackgroundAgentRunStatus.Pending,
            StartedAt    = DateTimeOffset.UtcNow,
        };
        await _state.WriteStateAsync();

        // Register in the parent run's index.
        var index = GrainFactory.GetGrain<IBackgroundAgentIndexGrain>(parentRunId);
        await index.AddHandleAsync(handle);

        // Schedule execution as a separate grain turn (P2: schedule next step as self-call).
        var self = GrainFactory.GetGrain<IBackgroundAgentRunGrain>(handle);
        _ = self.RunAsync();

        return handle;
    }

    /// <inheritdoc />
    public async Task RunAsync()
    {
        if (_state.State.Status is BackgroundAgentRunStatus.Cancelled or
            BackgroundAgentRunStatus.Completed or BackgroundAgentRunStatus.Failed)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _state.State.Status = BackgroundAgentRunStatus.Running;
        await _state.WriteStateAsync();

        try
        {
            if (_state.State.CancellationRequested)
            {
                await _cts.CancelAsync();
            }

            _cts.Token.ThrowIfCancellationRequested();

            var context = _state.State.ChildContext ?? new AgentContext();
            var childGrain = GrainFactory.GetGrain<IAiAgentGrain>(
                OrleansSessionGrainKey.Build(_state.State.ChildAgentId, _state.State.Handle));

            var result = await CollectStreamAsync(
                childGrain.StreamAgentAsync(_state.State.Message, context, _cts.Token),
                _cts.Token);

            _state.State.Status      = BackgroundAgentRunStatus.Completed;
            _state.State.Result      = result;
            _state.State.CompletedAt = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException)
        {
            _state.State.Status      = BackgroundAgentRunStatus.Cancelled;
            _state.State.CompletedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            _state.State.Status      = BackgroundAgentRunStatus.Failed;
            _state.State.Error       = ex.Message;
            _state.State.CompletedAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
        }

        await _state.WriteStateAsync();

        // Clean up the child session grain after the run.
        _ = GrainFactory
            .GetGrain<IAiAgentGrain>(OrleansSessionGrainKey.Build(_state.State.ChildAgentId, _state.State.Handle))
            .DeleteAsync();
    }

    /// <inheritdoc />
    public async Task<bool> CancelAsync()
    {
        if (_state.State.Status is BackgroundAgentRunStatus.Completed
            or BackgroundAgentRunStatus.Failed
            or BackgroundAgentRunStatus.Cancelled)
        {
            return false;
        }

        _state.State.CancellationRequested = true;
        await _state.WriteStateAsync();

        _cts?.Cancel();
        return true;
    }

    /// <inheritdoc />
    public Task<BackgroundAgentRunRecord?> GetAsync()
    {
        if (string.IsNullOrEmpty(_state.State.Handle))
            return Task.FromResult<BackgroundAgentRunRecord?>(null);

        return Task.FromResult<BackgroundAgentRunRecord?>(new BackgroundAgentRunRecord(
            Handle:       _state.State.Handle,
            ParentRunId:  _state.State.ParentRunId,
            ChildAgentId: _state.State.ChildAgentId,
            Status:       _state.State.Status,
            StartedAt:    _state.State.StartedAt,
            CompletedAt:  _state.State.CompletedAt,
            Result:       _state.State.Result,
            Error:        _state.State.Error));
    }

    private static async Task<string> CollectStreamAsync(
        IAsyncEnumerable<AgentEvent> stream,
        CancellationToken ct)
    {
        await foreach (var @event in stream.WithCancellation(ct))
        {
            if (@event is TurnCompleted tc)
                return tc.AssistantText;
            if (@event is TurnFailed tf)
                throw new InvalidOperationException($"{tf.ErrorType}: {tf.ErrorMessage}");
        }
        return string.Empty;
    }
}
