// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Plugins.Container.Preprocessing;

internal sealed class HistoryAssembler : IAgentPreprocessor
{
    public int Order => 0;

    public ValueTask<IReadOnlyList<ChatTurn>> ProcessAsync(
        AgentPreprocessorContext context,
        IReadOnlyList<ChatTurn> messages,
        CancellationToken cancellationToken = default)
    {
        var history = context.GrainState.History;
        if (history.Count == 0)
            return ValueTask.FromResult(messages);

        var result = new ChatTurn[history.Count + messages.Count];
        for (var i = 0; i < history.Count; i++) result[i] = history[i];
        for (var i = 0; i < messages.Count; i++) result[history.Count + i] = messages[i];
        return ValueTask.FromResult<IReadOnlyList<ChatTurn>>(result);
    }
}
