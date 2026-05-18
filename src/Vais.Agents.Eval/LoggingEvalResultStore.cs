// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Vais.Agents.Eval;

/// <summary>
/// In-memory <see cref="IEvalResultStore"/> that logs each result and keeps results
/// in a concurrent dictionary for the lifetime of the process. Suitable for
/// development and single-silo deployments. Replace with a Postgres-backed store for
/// multi-silo cross-restart durability.
/// </summary>
public sealed class LoggingEvalResultStore : IEvalResultStore
{
    private readonly ILogger<LoggingEvalResultStore> _logger;
    private readonly ConcurrentDictionary<string, EvalRunSummary> _summaries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<EvalCaseResultRecord>> _cases = new(StringComparer.Ordinal);

    /// <summary>DI ctor.</summary>
    public LoggingEvalResultStore(ILogger<LoggingEvalResultStore> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc/>
    public ValueTask AppendRunAsync(EvalRunSummary run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        _summaries[run.EvalRunId] = run;
        _cases.TryAdd(run.EvalRunId, new List<EvalCaseResultRecord>());
        _logger.LogInformation("Eval run {EvalRunId} status={Status} suite={Suite}", run.EvalRunId, run.Status, run.SuiteName);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask AppendCaseResultAsync(EvalCaseResultRecord result, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var list = _cases.GetOrAdd(result.EvalRunId, _ => new List<EvalCaseResultRecord>());
        lock (list) list.Add(result);
        _logger.LogInformation("Eval case {EvalRunId}/{CaseId} status={Status}", result.EvalRunId, result.CaseId, result.Status);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<EvalRunSummary>> ListRunsAsync(string? suiteName = null, int limit = 50, string? source = null, CancellationToken ct = default)
    {
        var items = _summaries.Values
            .Where(s => suiteName is null || string.Equals(s.SuiteName, suiteName, StringComparison.Ordinal))
            .Where(s => source is null || string.Equals(s.Source, source, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.StartedAt)
            .Take(limit)
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<EvalRunSummary>>(items);
    }

    /// <inheritdoc/>
    public ValueTask<EvalRunDetail?> GetRunAsync(string evalRunId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evalRunId);
        if (!_summaries.TryGetValue(evalRunId, out var summary))
            return ValueTask.FromResult<EvalRunDetail?>(null);

        IReadOnlyList<EvalCaseResultRecord> cases = Array.Empty<EvalCaseResultRecord>();
        if (_cases.TryGetValue(evalRunId, out var list))
            lock (list) cases = list.ToArray();

        return ValueTask.FromResult<EvalRunDetail?>(new EvalRunDetail(summary, cases));
    }
}
