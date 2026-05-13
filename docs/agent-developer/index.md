# Agent developer

Audience: you're building an agent.

You'll deploy a declarative agent against the runtime, route every model call and tool call through observable gateways, drop into Python when YAML isn't enough, and compose multiple agents into a multi-agent system (MAS) graph.

## Path

1. **[Your first declarative agent](../getting-started/deploy-your-first-agent.md)** — write an `Agent` manifest, `vais apply`, `vais invoke`. No C# required.
2. **[LLM gateway](../guides/plug-in-gateway-middleware.md)** — every model call passes through middleware: logging, OTel, rate limit, fallback. Wire your first middleware.
3. **[MCP gateway](../guides/gate-tool-calls-with-the-tool-gateway.md)** — every tool call passes through middleware: logging, OTel, rate limit, truncation. Wire MCP tools to an agent and observe them.
4. **[Simple Python agent](../guides/package-a-python-agent.md)** — drop into Python when YAML isn't enough. Same declarative shape; supervisor handles durability.
5. **[Multi-agent graph](../tutorials/from-zero-to-graph-in-20-minutes.md)** — compose multiple agents into a graph: sequential nodes, conditional edges, shared state.

> The five pages above are the existing material these tutorials are based on. Phase 3 of the docs reorganization reframes them as section-1 tutorials with consistent "you'll build X" leads; the underlying content is mostly the same.

## After this section

- Authoring custom plugin code (C# / LangGraph / Go) → [Deep agent development](../deep-development/index.md)
- Customizing runtime seams (middleware) → [Extensions](../extensions/index.md)
- Running the runtime yourself → [DevOps / admin](../devops/index.md)

## Related

- [Concepts → Declarative agents](../concepts/declarative-agents.md) — the manifest model.
- [Concepts → Gateway config control plane](../concepts/gateway-config-control-plane.md) — why every LLM and tool call goes through middleware.
- [Concepts → Graph orchestration](../concepts/graph-orchestration.md) — the Pregel/BSP graph model.
- [Reference → Manifest schema](../reference/manifest-schema.md) — manifest field reference.
