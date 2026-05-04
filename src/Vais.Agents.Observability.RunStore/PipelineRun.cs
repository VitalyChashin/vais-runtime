// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Observability.RunStore;

/// <summary>Snapshot of a single graph pipeline run as stored in the run store.</summary>
/// <param name="RunId">Unique run identifier.</param>
/// <param name="GraphId">Graph manifest identifier.</param>
/// <param name="Status">Current lifecycle status.</param>
/// <param name="StartedAt">UTC timestamp when the run started.</param>
/// <param name="EndedAt">UTC timestamp when the run ended, or <see langword="null"/> if still running.</param>
/// <param name="DurationMs">Elapsed milliseconds from start to end, or <see langword="null"/> if still running.</param>
/// <param name="SuperSteps">Number of super-steps completed.</param>
/// <param name="Error">Error message or interrupt ID, or <see langword="null"/> when successful.</param>
public sealed record PipelineRun(
    string RunId,
    string GraphId,
    RunStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    long? DurationMs,
    int SuperSteps,
    string? Error);
