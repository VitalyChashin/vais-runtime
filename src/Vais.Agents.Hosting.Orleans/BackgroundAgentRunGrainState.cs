// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>Persisted state for <see cref="BackgroundAgentRunGrain"/>.</summary>
[GenerateSerializer]
public sealed class BackgroundAgentRunGrainState
{
    /// <summary>Durable handle — equals the child session id / grain key.</summary>
    [Id(0)] public string Handle { get; set; } = string.Empty;

    /// <summary>Run id of the coordinator that started this sub-run.</summary>
    [Id(1)] public string ParentRunId { get; set; } = string.Empty;

    /// <summary>Agent id of the sub-agent.</summary>
    [Id(2)] public string ChildAgentId { get; set; } = string.Empty;

    /// <summary>User message to deliver to the sub-agent.</summary>
    [Id(3)] public string Message { get; set; } = string.Empty;

    /// <summary>Child context propagated from the coordinator at enqueue time.</summary>
    [Id(4)] public AgentContext? ChildContext { get; set; }

    /// <summary>Current lifecycle status.</summary>
    [Id(5)] public BackgroundAgentRunStatus Status { get; set; } = BackgroundAgentRunStatus.Pending;

    /// <summary>When the run was enqueued.</summary>
    [Id(6)] public DateTimeOffset StartedAt { get; set; }

    /// <summary>When the run reached a terminal status, or <c>null</c> if still active.</summary>
    [Id(7)] public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Final text result on success, or <c>null</c>.</summary>
    [Id(8)] public string? Result { get; set; }

    /// <summary>Error message on failure, or <c>null</c>.</summary>
    [Id(9)] public string? Error { get; set; }

    /// <summary>Whether cancellation was requested via <c>CancelAsync</c>.</summary>
    [Id(10)] public bool CancellationRequested { get; set; }
}
