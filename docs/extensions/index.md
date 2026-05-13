# Extensions

Audience: you're customizing the runtime's extension seams.

The runtime exposes named seams for cross-cutting concerns. The two gateway middleware chains (LLM, MCP) are the most-used and get concrete tutorials below; the other seams are listed in the catalog so you know where to plug in even though they aren't tutorialized in this pass.

## Concrete tutorials

1. **[Author an LLM gateway middleware](../guides/plug-in-gateway-middleware.md)** — `IGatewayMiddleware` contract, registration, composition order. Reference implementations to study: `Vais.Agents.Gateways.Fallback`, `Vais.Agents.Gateways.SemanticCache`, `Vais.Agents.Gateways.StructuredOutput`.
2. **[Author an MCP gateway middleware](../guides/gate-tool-calls-with-the-tool-gateway.md)** — same shape for tool calls. Reference implementations: `Vais.Agents.Gateways.McpCache`, `Vais.Agents.Gateways.McpReliability`, `Vais.Agents.Gateways.McpSecurity`, `Vais.Agents.Gateways.McpTransformation`.

## Other extension seams (catalog)

Named seams not tutorialized in this pass. Each entry points at the concept page (the "why") and names the interface so you can find the seam in source.

- **Input middleware** (`IAgentInputMiddleware`) — memory injection, retrieval, redaction. Plugins receive only the prepared context; this is the seam that prepares it. See [`concepts/session.md`](../concepts/session.md).
- **Guardrails** (`IInputGuardrail`, `IOutputGuardrail`, `IToolGuardrail`) — input / output / tool-call gating. See [`concepts/guardrails.md`](../concepts/guardrails.md).
- **Completion providers** (`ICompletionProvider`) — implement against a non-SK / non-MAF backend. See [`concepts/architecture.md`](../concepts/architecture.md).
- **Session stores** (`IAgentSession`) — custom conversation-history backends.
- **History reducers** (`IHistoryReducer`) — compress conversation history before sending to the model.
- **Prompt composers** (`ISystemPromptComposer`) — assemble the system prompt from layered contributors. See [`concepts/prompt.md`](../concepts/prompt.md).
- **Graph predicate operators** — extend the graph manifest's edge-predicate vocabulary. See [`reference/graph-predicate-operators.md`](../reference/graph-predicate-operators.md).
- **Policy engines** (`IAgentPolicyEngine`) — gate verbs through a custom policy. See [`concepts/opa-policy-engine.md`](../concepts/opa-policy-engine.md) for the reference OPA implementation.
- **Event subscribers** (`IAgentEventBus`, `IAgentGraphEventBus`) — react to per-agent or graph-lifecycle events. See [`reference/events.md`](../reference/events.md).

## Related

- [Concepts → Execution loop](../concepts/execution-loop.md) — where the seams sit inside the agent's turn.
- [Concepts → Gateway config control plane](../concepts/gateway-config-control-plane.md) — composition rules for middleware chains.
