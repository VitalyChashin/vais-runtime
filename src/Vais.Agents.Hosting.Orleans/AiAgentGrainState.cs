// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Persisted state for <see cref="AiAgentGrain"/>: the mutable bits of an
/// <see cref="IAiAgent"/> that must survive grain deactivation.
/// </summary>
[GenerateSerializer]
public sealed class AiAgentGrainState : IAgentGrainStateView
{
    /// <summary>
    /// Last known system prompt. When null, the grain falls back to the value
    /// supplied by the options factory at activation time.
    /// </summary>
    [Id(0)]
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// Conversation history in order. Re-populated into <see cref="Core.StatefulAiAgent"/>
    /// via <see cref="Core.StatefulAgentOptions.InitialHistory"/> on grain activation, and
    /// written back after every turn.
    /// </summary>
    [Id(1)]
    public List<ChatTurn> History { get; set; } = new();

    /// <summary>
    /// Opaque state blob for <see cref="Core.IOpaqueStateCarrier"/> agents (e.g. Python
    /// agent plugins that carry a LangGraph snapshot). <see langword="null"/> when not
    /// applicable or not yet set. Stored and returned verbatim; the agent interprets it.
    /// </summary>
    [Id(2)]
    public string? OpaqueState { get; set; }

    // IAgentGrainStateView — explicit to avoid ambiguity with the mutable List<ChatTurn> property.
    IReadOnlyList<ChatTurn> IAgentGrainStateView.History => History;
}
