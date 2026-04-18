// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents;

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
/// </remarks>
/// <param name="TextDelta">Next fragment of assistant text. Non-null; may be empty.</param>
/// <param name="ModelId">Model id, typically populated on the final update only.</param>
/// <param name="PromptTokens">Prompt-side token count, typically populated on the final update only.</param>
/// <param name="CompletionTokens">Completion-side token count, typically populated on the final update only.</param>
public sealed record CompletionUpdate(
    string TextDelta,
    string? ModelId = null,
    int? PromptTokens = null,
    int? CompletionTokens = null);
