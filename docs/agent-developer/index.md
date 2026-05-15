# Agent developer

Audience: you're building an agent.

You'll deploy a declarative agent against the runtime, route every model call and tool call through observable gateways, drop into Python when YAML isn't enough, and compose multiple agents into a multi-agent system (MAS) graph.

## Path

1. **[Your first declarative agent](your-first-declarative-agent.md)** — write an `Agent` manifest, `vais apply`, `vais invoke`. No C# required.
2. **[Wire the LLM gateway](wire-the-llm-gateway.md)** — route every model call through observable middleware via an `LlmGatewayConfig` manifest.
3. **[Wire the MCP gateway](wire-the-mcp-gateway.md)** — give the agent tools through an `McpGatewayConfig` middleware chain — logging, OTel, rate limit, truncation.
4. **[Ship a simple Python agent](ship-a-python-agent.md)** — drop into Python when YAML isn't enough. Same operator surface; supervisor handles durability.
5. **[Compose a multi-agent graph](compose-a-multi-agent-graph.md)** — sequential nodes, edges, shared state. `vais invoke-graph --stream` emits a structured event log.
6. **[Route graph edges with PowerFx](route-graph-edges-with-powerfx.md)** — inline `=...` expressions for conditional routing the `PropertyMatcher` vocabulary can't express; quality loop with retry budget.
7. **[Shape output with schema-guided reasoning](shape-output-with-sgr.md)** — bake the reasoning order into the JSON schema; cascade structured fields through state bindings; route on extracted fields; validate every reply with the `StructuredOutput` middleware.
8. **[Dispatch from a graph node with agent-as-tool](dispatch-from-a-graph-node.md)** — let the coordinator's LLM pick among sibling specialists via `localAgents`; one-field flip to background fan-out.
9. **[Connect OpenWebUI](connect-openwebui.md)** — point OpenWebUI at the runtime's OpenAI-compatible endpoint; chat with agents and graphs from a browser UI.

## After this section

- Authoring custom plugin code (C# / LangGraph / Go) → [Deep agent development](../deep-development/index.md)
- Customizing runtime seams (middleware) → [Extensions](../extensions/index.md)
- Running the runtime yourself → [DevOps / admin](../devops/index.md)

## Related

- [Concepts → Declarative agents](../concepts/declarative-agents.md) — the manifest model.
- [Concepts → Gateway config control plane](../concepts/gateway-config-control-plane.md) — why every LLM and tool call goes through middleware.
- [Concepts → Graph orchestration](../concepts/graph-orchestration.md) — the Pregel/BSP graph model.
- [Reference → Manifest schema](../reference/manifest-schema.md) — manifest field reference.
