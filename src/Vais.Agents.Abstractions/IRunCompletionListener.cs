// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents;

/// <summary>
/// Receives a signal each time a production agent turn or graph run completes.
/// Implementations are discovered from DI and fan-out is driven by
/// <c>RunCompletionEventBusBridge</c>. Registered as singletons — must be thread-safe.
/// </summary>
public interface IRunCompletionListener
{
    /// <summary>Called once per completed production run. Must not throw.</summary>
    ValueTask OnRunCompletedAsync(RunCompletionSignal signal, CancellationToken ct);
}

/// <summary>
/// Lightweight descriptor emitted for every completed production turn or graph run.
/// Built from <see cref="TurnCompleted"/> / <see cref="GraphCompleted"/> events by
/// <c>RunCompletionEventBusBridge</c>.
/// </summary>
/// <param name="AgentRunId">Agent or graph run id — matches <see cref="AgentContext.RunId"/>.</param>
/// <param name="AgentRef">Agent id when this is a turn completion; null for graph completions.</param>
/// <param name="GraphRef">Graph id when this is a graph completion; null for turn completions.</param>
/// <param name="WorkspaceId">Workspace scope from the run context.</param>
/// <param name="CompletedAt">UTC timestamp when the run completed.</param>
/// <param name="Duration">Wall-clock duration of the run.</param>
/// <param name="AssistantText">Last assistant text produced; present for <see cref="TurnCompleted"/>, null for <see cref="GraphCompleted"/>.</param>
/// <param name="FinalState">Terminal graph state; present for <see cref="GraphCompleted"/>, null for <see cref="TurnCompleted"/>.</param>
public sealed record RunCompletionSignal(
    string AgentRunId,
    string? AgentRef,
    string? GraphRef,
    string WorkspaceId,
    DateTimeOffset CompletedAt,
    TimeSpan Duration,
    string? AssistantText,
    IReadOnlyDictionary<string, JsonElement>? FinalState);
