// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Execution-loop flavour an agent runs under. Declarative counterpart of the three
/// agent archetypes surveyed during the v0.6 pillar scoping (parity with the SGR
/// project's <c>SGRAgent</c> / <c>ToolCallingAgent</c> / <c>SGRToolCallingAgent</c>
/// split).
/// </summary>
/// <remarks>
/// <para>
/// <b>v0.6 engine support.</b> Only <see cref="ToolCalling"/> is honoured at runtime
/// in v0.6. <see cref="SchemaGuided"/> and <see cref="SchemaGuidedToolCalling"/> are
/// contract-only — the manifest accepts and round-trips them, the engine treats
/// them as <see cref="ToolCalling"/>. Schema-guided execution lands with a later
/// reasoning pillar. Operators can set the field now to lock wire shape; upgrades
/// then enable behaviour without re-authoring manifests.
/// </para>
/// </remarks>
public enum AgentMode
{
    /// <summary>Classic tool-calling loop — free-form assistant text + model-requested tool calls. The shipped <c>StatefulAiAgent</c> behaviour.</summary>
    ToolCalling = 0,

    /// <summary>Schema-Guided Reasoning — LLM completes against <see cref="ReasoningSpec.Schema"/> as the primary response; no tool calling. Contract-only in v0.6.</summary>
    SchemaGuided = 1,

    /// <summary>SGR hybrid — each turn: schema-guided reasoning → tool decision → tool call → repeat. Contract-only in v0.6.</summary>
    SchemaGuidedToolCalling = 2,
}
