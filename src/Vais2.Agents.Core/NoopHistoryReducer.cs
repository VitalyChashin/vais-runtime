// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents.Core;

/// <summary>
/// Identity <see cref="IHistoryReducer"/>. Returns history unchanged. Default when
/// <see cref="StatefulAgentOptions.HistoryReducer"/> is null.
/// </summary>
public sealed class NoopHistoryReducer : IHistoryReducer
{
    /// <summary>Shared singleton instance. Stateless.</summary>
    public static readonly NoopHistoryReducer Instance = new();

    private NoopHistoryReducer() { }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ChatTurn>> ReduceAsync(
        IReadOnlyList<ChatTurn> history,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(history);
}
