// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Observability.AgentRunStore;

/// <summary>
/// Storage back-end for standalone agent invocation history.
/// Written to directly by the HTTP invoke handlers when a run starts, completes, or fails.
/// </summary>
public interface IAgentRunStore
{
    /// <summary>Idempotently creates the required schema. Called once on startup.</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Records that a standalone agent invocation has begun.</summary>
    Task StartRunAsync(string agentRunId, string agentId, string? inputText,
        string? userId, string? tenantId, string? correlationId,
        CancellationToken ct = default);

    /// <summary>Marks a run as completed and records the output text and token counts.</summary>
    Task CompleteRunAsync(string agentRunId, string? outputText,
        int inputTokens, int outputTokens, CancellationToken ct = default);

    /// <summary>Marks a run as failed and stores the error message.</summary>
    Task FailRunAsync(string agentRunId, string error, CancellationToken ct = default);

    /// <summary>Deletes runs whose <c>created_at</c> is older than <paramref name="cutoff"/>.</summary>
    Task DeleteRunsOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default);

    /// <summary>Lists runs for an agent, ordered by start time descending.</summary>
    Task<IReadOnlyList<AgentRun>> ListRunsAsync(string agentId,
        DateTimeOffset? since = null, DateTimeOffset? until = null, int limit = 20,
        CancellationToken ct = default);

    /// <summary>Returns a single run by its ID, or <see langword="null"/> if not found.</summary>
    Task<AgentRun?> GetRunAsync(string agentRunId, CancellationToken ct = default);
}
