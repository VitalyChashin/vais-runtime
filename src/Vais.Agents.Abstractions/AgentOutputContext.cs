// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Carries the response text, token usage, and identity metadata for a single
/// LLM call, available to every <see cref="AgentOutputMiddleware"/> in the chain.
/// Fires per LLM call (not per turn) — a tool-call loop with N round-trips
/// triggers N output-middleware invocations (per OQ-5).
/// </summary>
public sealed class AgentOutputContext
{
    /// <summary>The stable agent identifier for this invocation.</summary>
    public required string AgentId { get; init; }

    /// <summary>The run identifier stamped on this turn.</summary>
    public required string RunId { get; init; }

    /// <summary>The session identifier, or null when not in a session.</summary>
    public required string? SessionId { get; init; }

    /// <summary>
    /// The messages that formed the request for this LLM call (history snapshot
    /// including the current user turn and any tool-call/result turns).
    /// </summary>
    public required IReadOnlyList<ChatTurn> RequestMessages { get; init; }

    /// <summary>The assistant turn produced by this LLM call.</summary>
    public required ChatTurn ResponseMessage { get; init; }

    /// <summary>Token usage reported by the provider for this call, or null if not available.</summary>
    public TokenUsage? Usage { get; init; }

    /// <summary>
    /// Mutable property bag for passing state between middleware layers.
    /// Extension handlers write enrichment data here; downstream middleware reads from it.
    /// </summary>
    public IDictionary<string, object?> Properties { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
}

/// <summary>Token usage for a single LLM call.</summary>
/// <param name="InputTokens">Tokens consumed by the prompt/context.</param>
/// <param name="OutputTokens">Tokens produced by the completion.</param>
public sealed record TokenUsage(int? InputTokens, int? OutputTokens);
