// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents;

/// <summary>
/// Role of a participant in a conversation turn. Kept deliberately small —
/// <c>Tool</c> / <c>Function</c> roles are deferred to a later milestone when
/// tool-calling parity between adapters is in scope.
/// </summary>
/// <remarks>
/// Named <c>AgentChatRole</c> (not plain <c>ChatRole</c>) to avoid colliding with
/// <c>Microsoft.Extensions.AI.ChatRole</c> (MEAI's struct of the same short name)
/// at consumer sites that import both namespaces. The collision was the single
/// most-cited papercut in the dog-food findings from the 0.1.0/0.2.0 preview cuts.
/// </remarks>
public enum AgentChatRole
{
    /// <summary>A system-level instruction or guardrail message.</summary>
    System = 0,

    /// <summary>A message from the human end user.</summary>
    User = 1,

    /// <summary>A message produced by an AI agent / assistant.</summary>
    Assistant = 2,
}
