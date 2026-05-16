// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Eval;

/// <summary>
/// Persistence layer for eval run results. Default implementation is
/// <see cref="LoggingEvalResultStore"/> (no-op logging). Postgres impl ships in a
/// follow-on milestone.
/// </summary>
public interface IEvalResultStore
{
    /// <summary>Persist a new or updated run summary record.</summary>
    ValueTask AppendRunAsync(EvalRunSummary run, CancellationToken ct = default);

    /// <summary>Persist the result of a single case evaluation.</summary>
    ValueTask AppendCaseResultAsync(EvalCaseResultRecord result, CancellationToken ct = default);

    /// <summary>List run summaries, optionally filtered by suite name, newest first.</summary>
    ValueTask<IReadOnlyList<EvalRunSummary>> ListRunsAsync(string? suiteName = null, int limit = 50, CancellationToken ct = default);

    /// <summary>Fetch run detail including per-case results. Returns <see langword="null"/> when not found.</summary>
    ValueTask<EvalRunDetail?> GetRunAsync(string evalRunId, CancellationToken ct = default);
}
