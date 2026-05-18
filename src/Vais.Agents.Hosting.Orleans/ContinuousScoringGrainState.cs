// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>Durable state for <see cref="ContinuousScoringGrain"/>.</summary>
public sealed class ContinuousScoringGrainState
{
    /// <summary>Eval-runs row id for the currently open window. Null before the first sample.</summary>
    public string? CurrentWindowEvalRunId { get; set; }

    /// <summary>When the current window opened.</summary>
    public DateTimeOffset? CurrentWindowStart { get; set; }

    /// <summary>When the current window closes (= Start + WindowDuration).</summary>
    public DateTimeOffset? CurrentWindowEnd { get; set; }

    /// <summary>Samples scored within the current window.</summary>
    public int CurrentWindowSampledCount { get; set; }

    /// <summary>Samples that passed all assertions in the current window.</summary>
    public int CurrentWindowPassedCount { get; set; }

    /// <summary>Samples that failed one or more assertions in the current window.</summary>
    public int CurrentWindowFailedCount { get; set; }

    /// <summary>Suite manifest JSON snapshotted at grain activation. Re-read from registry if null.</summary>
    public string? SuiteJson { get; set; }

    /// <summary>Ordered queue of samples awaiting scoring. Persisted for crash-recovery.</summary>
    public List<PendingSample> PendingQueue { get; set; } = new();
}

/// <summary>An enqueued production run awaiting assertion evaluation.</summary>
public sealed class PendingSample
{
    /// <summary>Production agent or graph run id.</summary>
    public string ProductionRunId { get; set; } = string.Empty;

    /// <summary>When the production run completed.</summary>
    public DateTimeOffset CompletedAt { get; set; }

    /// <summary>Last assistant text from the turn; null for graph completions.</summary>
    public string? AssistantText { get; set; }

    /// <summary>Serialized final-state JSON; null for turn completions.</summary>
    public string? FinalStateJson { get; set; }
}
