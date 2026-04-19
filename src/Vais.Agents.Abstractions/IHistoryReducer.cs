// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Reduces a conversation history before it is handed to the completion provider.
/// Implementations decide the strategy — truncation by message count, truncation by
/// token budget, LLM-driven summarisation of older turns, or a hybrid.
/// </summary>
/// <remarks>
/// The reducer runs once per turn, on a snapshot of the session history. The returned
/// list is what gets sent to the provider; the session's own history is left intact,
/// so reduction is advisory / per-turn, never destructive.
/// </remarks>
public interface IHistoryReducer
{
    /// <summary>
    /// Return a (typically shorter) history suitable for sending to the provider.
    /// Implementations must preserve turn order; returning <paramref name="history"/>
    /// unchanged is valid and is the no-op default.
    /// </summary>
    ValueTask<IReadOnlyList<ChatTurn>> ReduceAsync(
        IReadOnlyList<ChatTurn> history,
        CancellationToken cancellationToken = default);
}
