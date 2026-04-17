// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents;

/// <summary>
/// Role of a participant in a conversation turn. Kept deliberately small —
/// <c>Tool</c> / <c>Function</c> roles are deferred to a later milestone when
/// tool-calling parity between adapters is in scope.
/// </summary>
public enum ChatRole
{
    /// <summary>A system-level instruction or guardrail message.</summary>
    System = 0,

    /// <summary>A message from the human end user.</summary>
    User = 1,

    /// <summary>A message produced by an AI agent / assistant.</summary>
    Assistant = 2,
}
