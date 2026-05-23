# Extensions

Audience: you're customizing the runtime's extension seams.

The runtime exposes named seams for cross-cutting concerns. The two gateway middleware chains (LLM, MCP) are the most-used and get concrete tutorials below; the other seams are listed in the catalog so you know where to plug in even though they aren't tutorialized in this pass.

## Concrete tutorials

1. **[Author an LLM gateway middleware](author-an-llm-gateway-middleware.md)** — `LlmGatewayMiddleware` base class, four hooks (non-streaming, streaming, per-delta, on-complete), worked example of a `PromptInjectionGuardMiddleware` short-circuit.
2. **[Author an MCP gateway middleware](author-an-mcp-gateway-middleware.md)** — `ToolGatewayMiddleware` base class, single override, worked examples of a `ToolLatencyAlertMiddleware` (observation) and a `TenantToolDenyMiddleware` (short-circuit).

Both gateway middlewares can be **DI-registered** (baked in at build time) or **hot-published as a `kind: Extension`** (`vais apply -f`, no restart) on the `llmGatewayMiddleware` / `toolGatewayMiddleware` seam — in C# (`host: csharp`) or a container (`host: container`, C#/Python). Each tutorial's "Author it as an extension" section shows the manifest; runnable samples live in [`samples/extensions/ext-tooldeny-csharp`](../../samples/extensions/ext-tooldeny-csharp) and [`ext-tooldeny-python`](../../samples/extensions/ext-tooldeny-python).
3. **[Agent input middleware](agent-input-middleware.md)** — `AgentInputMiddleware` base class, the P12 mandatory-inbound seam; reshape the user message before the agent receives it; registration options (singleton, named, direct).

## Other extension seams (catalog)

The seams below have a stable contract but no dedicated tutorial in this pass — the concept page plus the source-tree reference impls are usually enough.

→ **[Other extension seams](other-extension-seams.md)** — input middleware, guardrails, completion providers, session stores, history reducers, prompt composers, graph predicate operators, policy engines, event subscribers. Includes a "Picking the right seam" table.

## Related

- [Concepts → Execution loop](../concepts/execution-loop.md) — where the seams sit inside the agent's turn.
- [Concepts → Gateway config control plane](../concepts/gateway-config-control-plane.md) — composition rules for middleware chains.
