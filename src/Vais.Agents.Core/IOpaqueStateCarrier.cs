// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Core;

/// <summary>
/// Opt-in interface for <see cref="IAiAgent"/> implementations that carry an opaque
/// state blob across invocations. The hosting layer (e.g. <c>AiAgentGrain</c>) uses
/// this interface to persist and restore the blob alongside the agent's
/// <see cref="IAiAgent.History"/> so the agent's internal state survives grain
/// deactivation and silo relocation.
/// </summary>
/// <remarks>
/// <para>
/// Only implement this interface when your agent holds state that is not fully
/// captured by <see cref="IAiAgent.History"/>. The canonical example is a Python
/// agent that keeps a LangGraph state machine snapshot as a JSON blob.
/// </para>
/// <para>
/// The blob is treated as opaque by the hosting layer — it is stored and returned
/// verbatim. Agents are responsible for ensuring the blob is valid JSON (or any other
/// format they expect) and for handling <see langword="null"/> (first activation,
/// or after a reset).
/// </para>
/// </remarks>
public interface IOpaqueStateCarrier
{
    /// <summary>
    /// Gets or sets the opaque state blob. <see langword="null"/> means "no state yet"
    /// (first activation or after <see cref="IAiAgent.Reset"/>).
    /// </summary>
    string? OpaqueState { get; set; }
}
