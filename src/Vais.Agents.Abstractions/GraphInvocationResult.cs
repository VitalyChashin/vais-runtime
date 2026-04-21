// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents;

/// <summary>
/// Result of a completed (or interrupted) graph run. When <see cref="IsComplete"/>
/// is <see langword="false"/> the run is paused at an <c>Interrupt</c> node; callers
/// use <see cref="PendingInterruptId"/> + <see cref="RunId"/> to resume via
/// <c>IAgentGraphLifecycleManager.ResumeAsync</c>.
/// </summary>
/// <param name="RunId">Correlation id for the run — matches <see cref="AgentGraphEvent.RunId"/>.</param>
/// <param name="FinalState">
/// State at the point the run stopped (terminal node or interrupt node).
/// Null values in the bag indicate keys that were present in <c>InitialState</c>
/// but cleared during execution.
/// </param>
/// <param name="IsComplete">
/// <see langword="true"/> when the graph reached an <c>End</c> node;
/// <see langword="false"/> when paused at an <c>Interrupt</c> node.
/// </param>
/// <param name="PendingInterruptId">
/// Populated when <see cref="IsComplete"/> is <see langword="false"/>.
/// Matches <see cref="GraphInterrupted.InterruptId"/>; pass to
/// <see cref="GraphResumeRequest.InterruptId"/> to resume.
/// </param>
/// <param name="PendingInterruptNodeId">Node id at which the interrupt occurred.</param>
/// <param name="PendingInterruptReason">Human-readable reason supplied by the interrupt node.</param>
public sealed record GraphInvocationResult(
    string RunId,
    IDictionary<string, JsonElement> FinalState,
    bool IsComplete,
    string? PendingInterruptId = null,
    string? PendingInterruptNodeId = null,
    string? PendingInterruptReason = null);
