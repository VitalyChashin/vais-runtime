// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// One chunk of a streaming completion. Emitted repeatedly by
/// <see cref="IStreamingCompletionProvider.StreamAsync"/> until the stream completes.
/// </summary>
/// <remarks>
/// <para>
/// Most updates carry only a <see cref="TextDelta"/> — the next fragment of
/// assistant text to append. Token counts and model id are typically reported on
/// the final update (when the provider has them) and left null on intermediate
/// updates; consumers that aggregate should sum deltas and take the final
/// non-null metadata fields as authoritative.
/// </para>
/// <para>
/// <see cref="TextDelta"/> is always non-null; it may be empty for updates that
/// carry metadata only (e.g. a final update that reports token usage after the
/// last text chunk). Consumers joining deltas with simple string concatenation
/// get the expected result.
/// </para>
/// <para>
/// <b>Tool calls.</b> When a streaming turn ends with the model requesting tool
/// invocations, the provider emits a terminal <see cref="CompletionUpdate"/>
/// whose <see cref="ToolCalls"/> is non-null and carries the accumulated list
/// of <see cref="ToolCallRequest"/>s. <see cref="TextDelta"/> on that terminal
/// update is typically empty (tool-call turns rarely also emit text). Consumers
/// aggregating a stream should watch for any non-null <see cref="ToolCalls"/>
/// across the stream — the last non-null value wins, mirroring the metadata
/// aggregation rule.
/// </para>
/// </remarks>
/// <param name="TextDelta">Next fragment of assistant text. Non-null; may be empty.</param>
/// <param name="ModelId">Model id, typically populated on the final update only.</param>
/// <param name="PromptTokens">Prompt-side token count, typically populated on the final update only.</param>
/// <param name="CompletionTokens">Completion-side token count, typically populated on the final update only.</param>
/// <param name="ToolCalls">
/// Tool calls the model requested on this turn, typically populated on the
/// terminal update only. Null on intermediate text deltas and on final updates
/// that don't end with tool requests.
/// </param>
public sealed record CompletionUpdate(
    string TextDelta,
    string? ModelId = null,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    IReadOnlyList<ToolCallRequest>? ToolCalls = null);
