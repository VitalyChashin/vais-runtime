// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Opt-in post-creation hook for agent implementations that need a live reference to
/// the grain's persisted state (e.g. the preprocessing pipeline for container plugins).
/// Checked and called by <c>AiAgentGrain</c> immediately after the agent is created by
/// the factory, before <c>OnActivateAsync</c> returns.
/// </summary>
public interface IAgentGrainStateConsumer
{
    /// <summary>
    /// Called once per grain activation with a live, read-only view of the grain's
    /// in-memory state. The view reflects any mutations made between activations —
    /// callers must not cache the contents; read from it at call time.
    /// </summary>
    void SetGrainState(IAgentGrainStateView grainState);
}
