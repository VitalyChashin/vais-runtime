// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;

namespace Vais.Agents.CrossHostTests;

/// <summary>
/// Thread-safe <see cref="IUsageSink"/> that enqueues every received
/// <see cref="UsageRecord"/>. Used per-host to capture exactly what the host produced
/// during a scenario run; comparison across hosts drives the parity assertion.
/// </summary>
public sealed class RecordingUsageSink : IUsageSink
{
    private readonly ConcurrentQueue<UsageRecord> _records = new();

    public IReadOnlyList<UsageRecord> Records => _records.ToArray();

    public ValueTask ReportAsync(UsageRecord record, CancellationToken cancellationToken = default)
    {
        _records.Enqueue(record);
        return ValueTask.CompletedTask;
    }

    public void Clear()
    {
        while (_records.TryDequeue(out _)) { }
    }
}

/// <summary>
/// Thread-safe <see cref="IAgentFilter"/> that records the observed request's history
/// size and system prompt for each invocation, then forwards to the next stage. The
/// recorded strings are intentionally small-and-deterministic so parity comparison is
/// a simple sequence-equals on lists of strings.
/// </summary>
public sealed class RecordingFilter : IAgentFilter
{
    private readonly ConcurrentQueue<string> _invocations = new();

    public IReadOnlyList<string> Invocations => _invocations.ToArray();

    public async Task<CompletionResponse> InvokeAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
        CancellationToken cancellationToken)
    {
        _invocations.Enqueue(FormatInvocation(request));
        return await next(request, cancellationToken).ConfigureAwait(false);
    }

    public void Clear()
    {
        while (_invocations.TryDequeue(out _)) { }
    }

    private static string FormatInvocation(CompletionRequest request) =>
        $"history={request.History.Count},prompt={request.SystemPrompt ?? "<null>"}";
}
