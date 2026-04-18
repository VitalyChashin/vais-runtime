// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents.Hosting.Orleans;

/// <summary>
/// Persisted state for <see cref="AgentSessionGrain"/>: the per-session conversation
/// history. Other per-session state (checkpoints, interrupt metadata) will land here
/// in later pillars.
/// </summary>
[GenerateSerializer]
public sealed class AgentSessionGrainState
{
    /// <summary>Conversation history in order, written after every append.</summary>
    [Id(0)]
    public List<ChatTurn> History { get; set; } = new();
}
