// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;

namespace Vais.Agents.Core;

/// <summary>
/// In-process <see cref="IBackgroundAgentTracker"/>. Records are kept in a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>; runs execute on background
/// <see cref="Task"/> continuations.
/// <para>
/// <strong>P1 scaling gap — dev/test only.</strong> State is process-local: it is
/// not visible to sibling nodes, does not survive a process restart, and grows
/// indefinitely (no TTL). Use <c>OrleansBackgroundAgentTracker</c> in production.
/// </para>
/// </summary>
public sealed class InMemoryBackgroundAgentTracker : IBackgroundAgentTracker
{
    private readonly IAgentRuntime _runtime;
    private readonly ConcurrentDictionary<string, BackgroundAgentRunRecord> _records = new(StringComparer.Ordinal);

    /// <summary>Creates a new tracker backed by the supplied <paramref name="runtime"/>.</summary>
    public InMemoryBackgroundAgentTracker(IAgentRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
    }

    /// <inheritdoc />
    public ValueTask<string> StartAsync(
        string parentRunId,
        string childAgentId,
        string childSessionId,
        string message,
        AgentContext childContext,
        CancellationToken ct = default)
    {
        var record = new BackgroundAgentRunRecord(
            Handle: childSessionId,
            ParentRunId: parentRunId,
            ChildAgentId: childAgentId,
            Status: BackgroundAgentRunStatus.Pending,
            StartedAt: DateTimeOffset.UtcNow);

        _records[childSessionId] = record;

        // Fire and forget; errors are captured in the record.
        _ = RunAsync(childAgentId, childSessionId, message, childContext);

        return ValueTask.FromResult(childSessionId);
    }

    /// <inheritdoc />
    public ValueTask<BackgroundAgentRunRecord?> GetAsync(string handle, CancellationToken ct = default)
    {
        _records.TryGetValue(handle, out var record);
        return ValueTask.FromResult(record);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<BackgroundAgentRunRecord>> ListAsync(string parentRunId, CancellationToken ct = default)
    {
        IReadOnlyList<BackgroundAgentRunRecord> list = _records.Values
            .Where(r => string.Equals(r.ParentRunId, parentRunId, StringComparison.Ordinal))
            .ToList();
        return ValueTask.FromResult(list);
    }

    /// <inheritdoc />
    public ValueTask<bool> CancelAsync(string handle, CancellationToken ct = default)
    {
        if (!_cancellations.TryGetValue(handle, out var cts))
            return ValueTask.FromResult(false);

        cts.Cancel();
        return ValueTask.FromResult(true);
    }

    // ── internals ───────────────────────────────────────────────────────────

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new(StringComparer.Ordinal);

    private async Task RunAsync(string childAgentId, string childSessionId, string message, AgentContext childContext)
    {
        using var cts = new CancellationTokenSource();
        _cancellations[childSessionId] = cts;
        try
        {
            Transition(childSessionId, BackgroundAgentRunStatus.Running);

            var agent = _runtime.GetOrCreateForSession(childAgentId, childSessionId);
            using var _ = new AsyncLocalAgentContextAccessor().Push(childContext);
            var result = await agent.AskAsync(message, cts.Token);

            Transition(childSessionId, BackgroundAgentRunStatus.Completed, result: result);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Transition(childSessionId, BackgroundAgentRunStatus.Cancelled);
        }
        catch (Exception ex)
        {
            Transition(childSessionId, BackgroundAgentRunStatus.Failed, error: ex.Message);
        }
        finally
        {
            _cancellations.TryRemove(childSessionId, out _);
        }
    }

    private void Transition(string handle, BackgroundAgentRunStatus status, string? result = null, string? error = null)
    {
        _records.AddOrUpdate(
            handle,
            _ => throw new InvalidOperationException($"Record missing for handle '{handle}'."),
            (_, existing) => existing with
            {
                Status = status,
                CompletedAt = status is BackgroundAgentRunStatus.Completed
                    or BackgroundAgentRunStatus.Failed
                    or BackgroundAgentRunStatus.Cancelled
                    ? DateTimeOffset.UtcNow
                    : null,
                Result = result,
                Error = error,
            });
    }
}
