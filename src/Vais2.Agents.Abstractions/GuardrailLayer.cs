// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents;

/// <summary>
/// Which middleware layer raised a guardrail event or denial. Mirrors MAF's three-layer
/// middleware split (Agent / Function / Chat) in vocabulary that matches this library's
/// interfaces: <see cref="IInputGuardrail"/> → <see cref="Input"/>,
/// <see cref="IOutputGuardrail"/> → <see cref="Output"/>,
/// <see cref="IToolGuardrail"/> → <see cref="Tool"/>.
/// </summary>
public enum GuardrailLayer
{
    /// <summary>Ran on a fully-prepared <see cref="CompletionRequest"/> before the filter chain / provider call.</summary>
    Input = 0,

    /// <summary>Ran on a <see cref="CompletionResponse"/> after the provider returned, before the session append.</summary>
    Output = 1,

    /// <summary>Ran around an individual tool invocation (before or after). Not wired in v0.4 — lands with the execution-loop pillar.</summary>
    Tool = 2,
}
