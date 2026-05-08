// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Read-only view of the persisted agent grain state exposed to the preprocessing pipeline.
/// Implemented by <c>AiAgentGrainState</c> in <c>Vais.Agents.Hosting.Orleans</c>.
/// Defined here so <c>IAgentPreprocessor</c> has no Orleans dependency.
/// </summary>
public interface IAgentGrainStateView
{
    /// <summary>Last known system prompt. Null when the grain has not overridden the manifest default.</summary>
    string? SystemPrompt { get; }

    /// <summary>Conversation history in order.</summary>
    IReadOnlyList<ChatTurn> History { get; }

    /// <summary>
    /// Opaque plugin-private state blob (e.g. a LangGraph checkpoint snapshot).
    /// Null when not applicable or not yet set.
    /// </summary>
    string? OpaqueState { get; }
}
