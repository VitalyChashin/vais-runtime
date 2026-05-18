// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Per-continuous-suite Orleans grain. Grain key = suite id. Manages rolling
/// eval-run windows and scores each sampled production run via the suite's
/// assertion chain. One grain turn per sample (P2).
/// </summary>
public interface IContinuousScoringGrain : IGrainWithStringKey
{
    /// <summary>
    /// Enqueue a completed production run for scoring. The grain processes
    /// the queue one sample per turn to preserve P2 compliance.
    /// </summary>
    ValueTask EnqueueSampleAsync(
        string productionRunId,
        DateTimeOffset completedAt,
        string? assistantText,
        IReadOnlyDictionary<string, JsonElement>? finalState);

    /// <summary>
    /// Drain one item from the pending queue and score it. Self-scheduled
    /// after each <see cref="EnqueueSampleAsync"/> call; safe to call from tests.
    /// </summary>
    ValueTask ProcessNextAsync();
}
