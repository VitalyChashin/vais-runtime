// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// A single agent invocation's usage report, emitted to an <see cref="IUsageSink"/>.
/// Stack-neutral — fields are populated from whatever the underlying provider reported
/// plus what the runtime already knows (timings, agent identity, context).
/// </summary>
/// <param name="ProviderName">Value of <see cref="ICompletionProvider.ProviderName"/> that handled the turn.</param>
/// <param name="ModelId">Model identifier reported by the provider. May be "unknown".</param>
/// <param name="PromptTokens">Input tokens, if the provider reported them.</param>
/// <param name="CompletionTokens">Output tokens, if the provider reported them.</param>
/// <param name="Duration">Wall-clock time from request enqueue to response.</param>
/// <param name="StartedAt">UTC timestamp of when the turn began.</param>
/// <param name="Succeeded">Whether the turn completed without throwing.</param>
/// <param name="AgentName">
/// Human-readable agent identifier (e.g. a grain key or a DI-registered agent name).
/// May be null when the agent has no stable name.
/// </param>
/// <param name="UserId">User identity from <see cref="IAgentContextAccessor"/>, when present.</param>
/// <param name="TenantId">Tenant identity from <see cref="IAgentContextAccessor"/>, when present.</param>
/// <param name="CorrelationId">Correlation id for tying the turn to a broader operation, when present.</param>
/// <param name="ErrorType">Short exception type name on failure; null on success.</param>
public sealed record UsageRecord(
    string ProviderName,
    string ModelId,
    int? PromptTokens,
    int? CompletionTokens,
    TimeSpan Duration,
    DateTimeOffset StartedAt,
    bool Succeeded,
    string? AgentName = null,
    string? UserId = null,
    string? TenantId = null,
    string? CorrelationId = null,
    string? ErrorType = null)
{
    /// <summary>Total tokens, summing <see cref="PromptTokens"/> and <see cref="CompletionTokens"/>; null if both are null.</summary>
    public int? TotalTokens =>
        PromptTokens is null && CompletionTokens is null
            ? null
            : (PromptTokens ?? 0) + (CompletionTokens ?? 0);
}
