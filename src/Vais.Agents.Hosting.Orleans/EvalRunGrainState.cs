// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Vais.Agents.Eval;

namespace Vais.Agents.Hosting.Orleans;

/// <summary>Durable state for <see cref="EvalRunGrain"/>.</summary>
[GenerateSerializer]
public sealed class EvalRunGrainState
{
    /// <summary>Suite id.</summary>
    [Id(0)] public string? SuiteName { get; set; }
    /// <summary>Suite version string.</summary>
    [Id(1)] public string? SuiteVersion { get; set; }
    /// <summary>Serialized <see cref="EvalSuiteManifest"/> JSON for rehydration after silo restart.</summary>
    [Id(2)] public string? SuiteJson { get; set; }
    /// <summary>Index of the next case to process.</summary>
    [Id(3)] public int CurrentCaseIndex { get; set; }
    /// <summary>Lifecycle status of this run.</summary>
    [Id(4)] public EvalRunStatus Status { get; set; } = EvalRunStatus.Pending;
    /// <summary>When the run was started.</summary>
    [Id(5)] public DateTimeOffset StartedAt { get; set; }
    /// <summary>When the run completed, failed, or was cancelled.</summary>
    [Id(6)] public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>Total number of cases in the suite.</summary>
    [Id(7)] public int TotalCases { get; set; }
    /// <summary>Number of cases where all assertions passed.</summary>
    [Id(8)] public int PassedCases { get; set; }
    /// <summary>Number of cases where one or more assertions failed.</summary>
    [Id(9)] public int FailedCases { get; set; }
    /// <summary>Workspace under which the run was started.</summary>
    [Id(10)] public string? Workspace { get; set; }
}
