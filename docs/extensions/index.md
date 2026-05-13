# Extensions

Audience: you're customizing the runtime's extension seams.

The runtime exposes named seams for cross-cutting concerns. The two gateway middleware chains (LLM, MCP) are the most-used and get concrete tutorials below; the other seams are listed in the catalog so you know where to plug in even though they aren't tutorialized in this pass.

## Concrete tutorials

1. **[Author an LLM gateway middleware](author-an-llm-gateway-middleware.md)** — `LlmGatewayMiddleware` base class, four hooks (non-streaming, streaming, per-delta, on-complete), worked example of a `PromptInjectionGuardMiddleware` short-circuit.
2. **[Author an MCP gateway middleware](author-an-mcp-gateway-middleware.md)** — `ToolGatewayMiddleware` base class, single override, worked examples of a `ToolLatencyAlertMiddleware` (observation) and a `TenantToolDenyMiddleware` (short-circuit).

## Other extension seams (catalog)

The seams below have a stable contract but no dedicated tutorial in this pass — the concept page plus the source-tree reference impls are usually enough.

→ **[Other extension seams](other-extension-seams.md)** — input middleware, guardrails, completion providers, session stores, history reducers, prompt composers, graph predicate operators, policy engines, event subscribers. Includes a "Picking the right seam" table.

## Related

- [Concepts → Execution loop](../concepts/execution-loop.md) — where the seams sit inside the agent's turn.
- [Concepts → Gateway config control plane](../concepts/gateway-config-control-plane.md) — composition rules for middleware chains.
