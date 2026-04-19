// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Declarative handoff target — names another agent this manifest can delegate to,
/// with an optional routing hint. Lands on <see cref="AgentManifest.Handoffs"/>;
/// the runtime resolves target agents by id at invocation time.
/// </summary>
/// <param name="ToAgent">Target agent id — must resolve to another <see cref="AgentManifest"/> in the same registry.</param>
/// <param name="When">
/// Free-form natural-language hint describing when this handoff applies
/// (e.g. <c>"user asks about invoices or refunds"</c>). Used by the orchestrator
/// today; may become a routing LLM prompt in a later pillar.
/// </param>
/// <param name="CarryHistory">
/// When true, forwarding the conversation history to the target agent; when false
/// or null, starts the target with a fresh session. Defaults to runtime policy.
/// </param>
public sealed record HandoffRef(
    string ToAgent,
    string? When = null,
    bool? CarryHistory = null);
