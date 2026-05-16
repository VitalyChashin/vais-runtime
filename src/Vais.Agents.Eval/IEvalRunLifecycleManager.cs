// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Eval;

/// <summary>
/// Control plane for eval run lifecycle — start, cancel, list, and fetch runs.
/// In-process implementation backed by <c>IEvalRunGrain</c>;
/// HTTP client implementation calls the REST API.
/// </summary>
public interface IEvalRunLifecycleManager
{
    /// <summary>Start a new eval run for the named suite. Returns the new eval run id.</summary>
    ValueTask<string> StartRunAsync(string suiteName, string workspace, CancellationToken ct = default);

    /// <summary>Request cancellation of a running eval run.</summary>
    ValueTask CancelRunAsync(string evalRunId, CancellationToken ct = default);

    /// <summary>Fetch full run detail including per-case results. Null on miss.</summary>
    ValueTask<EvalRunDetail?> GetRunDetailAsync(string evalRunId, CancellationToken ct = default);

    /// <summary>List runs, optionally filtered by suite name.</summary>
    ValueTask<IReadOnlyList<EvalRunSummary>> ListRunsAsync(string? suiteName = null, int limit = 50, CancellationToken ct = default);
}
