// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Instantiation;

/// <summary>
/// The per-agent middleware chains + run budget resolved from an agent's manifest.
/// Returned by <see cref="IAgentManifestTranslator.ResolvePerAgentChainsAsync"/>; consumed by the
/// container-gateway endpoints to honour <c>LlmGatewayRef</c>, <c>McpGatewayRef</c>,
/// <c>OntologyRef</c>-bound south cartridge, and Plan C2 delegation-governance for plugin
/// agents — symmetric with how <c>StatefulAiAgent</c> consumes them for in-process agents.
/// </summary>
/// <param name="Llm">LLM gateway chain. When the manifest has no <c>LlmGatewayRef</c>, this is the DI-global chain.</param>
/// <param name="Tool">Tool gateway chain, including the south cartridge + Plan C2 governance appends when applicable.</param>
/// <param name="Input">Agent input middleware chain — populated when Plan C2 capability fabric auto-wires for a coordinator.</param>
/// <param name="Budget">Per-agent run budget, if declared on the manifest.</param>
public sealed record PerAgentChains(
    IReadOnlyList<LlmGatewayMiddleware> Llm,
    IReadOnlyList<ToolGatewayMiddleware> Tool,
    IReadOnlyList<AgentInputMiddleware> Input,
    RunBudget? Budget);
