// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// What an <see cref="IContextProvider"/> contributes to a turn. All fields are
/// additive and optional; the host merges contributions from every provider in the
/// configured chain into the candidate <see cref="CompletionRequest"/> before it
/// reaches the model.
/// </summary>
/// <param name="SystemPromptAddendum">
/// Text appended to the candidate's system prompt with a <c>"\n\n"</c> separator.
/// Multiple providers' addenda are concatenated in provider order.
/// </param>
/// <param name="InjectedHistory">
/// Extra turns to append <em>after</em> the candidate's history. The most-recent
/// user turn remains at the tail. Intended for retrieved-context injection, prior
/// cross-session summaries, or few-shot example pairs.
/// </param>
/// <param name="AdditionalTools">
/// Tools to append to the candidate's tool list, if any. Duplicate names are the
/// caller's concern — providers should namespace their tool names when needed.
/// </param>
public sealed record ContextContribution(
    string? SystemPromptAddendum = null,
    IReadOnlyList<ChatTurn>? InjectedHistory = null,
    IReadOnlyList<ITool>? AdditionalTools = null)
{
    /// <summary>A contribution that changes nothing. Useful sentinel for providers whose work is conditional.</summary>
    public static ContextContribution Empty { get; } = new();
}
