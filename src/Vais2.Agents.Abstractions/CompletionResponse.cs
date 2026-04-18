// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents;

/// <summary>
/// Result of a single-turn completion. Stack-neutral.
/// </summary>
/// <param name="Text">Assistant-produced text. Non-null; may be empty on error/refusal.</param>
/// <param name="ModelId">
/// Identifier of the model that produced the response, if known to the adapter.
/// Example: "gpt-4o-mini".
/// </param>
/// <param name="PromptTokens">Tokens consumed by the input, if reported by the provider.</param>
/// <param name="CompletionTokens">Tokens consumed by the output, if reported by the provider.</param>
/// <param name="ToolCalls">
/// Tool calls the model requested in this response, if any. Populated only when
/// the provider is configured to surface tool calls rather than auto-invoke them
/// (the SK and MAF adapters do this starting in v0.4). Null or empty means the
/// assistant produced a final text response; <c>StatefulAiAgent</c>'s outer
/// loop exits in that case. When non-empty, the loop dispatches each call via
/// <see cref="IToolCallDispatcher"/> and re-invokes the provider on the next round.
/// </param>
public sealed record CompletionResponse(
    string Text,
    string? ModelId = null,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    IReadOnlyList<ToolCallRequest>? ToolCalls = null)
{
    /// <summary>
    /// Total tokens consumed, derived from <see cref="PromptTokens"/> and
    /// <see cref="CompletionTokens"/>. Returns null only when both inputs are null.
    /// </summary>
    public int? TotalTokens =>
        PromptTokens is null && CompletionTokens is null
            ? null
            : (PromptTokens ?? 0) + (CompletionTokens ?? 0);
}
