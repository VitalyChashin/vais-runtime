// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Orleans grain that owns a single durable background-agent sub-run.
/// Grain key = the child session id (= the handle returned to the coordinator).
/// </summary>
/// <remarks>
/// P1 guarantee: the run grain persists its <see cref="BackgroundAgentRunRecord"/>
/// to durable storage. On silo restart, activation detects a
/// <see cref="BackgroundAgentRunStatus.Pending"/> or
/// <see cref="BackgroundAgentRunStatus.Running"/> status and re-schedules execution.
/// Because the child invocation uses a journaled grain turn keyed by the handle,
/// tool calls and completion replay from the journal — no LLM re-execution.
/// </remarks>
public interface IBackgroundAgentRunGrain : IGrainWithStringKey
{
    /// <summary>
    /// Persist the run record as <see cref="BackgroundAgentRunStatus.Pending"/>,
    /// register in the index grain, and schedule <see cref="RunAsync"/> as a
    /// self-call. Returns the handle.
    /// </summary>
    Task<string> StartAsync(
        string parentRunId,
        string childAgentId,
        string message,
        AgentContext childContext);

    /// <summary>
    /// Execute the child agent invocation (one grain turn; may span 30–120 s).
    /// Sets status <see cref="BackgroundAgentRunStatus.Running"/> →
    /// <see cref="BackgroundAgentRunStatus.Completed"/> /
    /// <see cref="BackgroundAgentRunStatus.Failed"/> /
    /// <see cref="BackgroundAgentRunStatus.Cancelled"/>.
    /// </summary>
    Task RunAsync();

    /// <summary>Request cancellation. Returns <c>true</c> if the run was active.</summary>
    Task<bool> CancelAsync();

    /// <summary>Return the current run record snapshot.</summary>
    Task<BackgroundAgentRunRecord?> GetAsync();
}
