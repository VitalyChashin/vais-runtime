# Library mode

Audience: you're embedding `Vais.Agents` primitives in your own .NET host instead of running the standalone runtime.

> Most users want [Agent developer](../agent-developer/index.md) instead. Library mode is for hosts that need to embed the agent classes directly — uncommon, but supported as a first-class shape.

## Path

1. **[Install + package picks](installation.md)** — which `Vais.Agents.*` NuGet packages to install for which scenario.
2. **[30-second library hello](hello-agent.md)** — `new StatefulAiAgent(...)` against Semantic Kernel, then against Microsoft Agent Framework. Same agent class; swap the adapter via DI.
3. **[Choose your stack — MAF or SK](choose-your-stack.md)** — decision framework. Relevant only in library mode; the runtime hides this choice from manifest authors.

## What you give up

By embedding instead of running the runtime, you opt out of:

- Declarative manifests (`kind: Agent`, `kind: AgentGraph`) and `vais apply` lifecycle.
- The `vais` CLI and the Kubernetes operator.
- Durable Orleans hosting — unless you wire Orleans into your host yourself.
- The LLM and MCP gateway middleware chains — unless you wire them yourself.
- Agent-as-MCP-tool and agent-as-A2A-endpoint hosting — unless you compose the server packages yourself.

The library is genuinely usable without the runtime — it predates the runtime tier and stays first-class. But the runtime is what makes most of the project's positioning work; consider it before committing to library mode.

## Related

- [Concepts → Architecture](../concepts/architecture.md) — the 32-package layering and dependency rules.
- [Reference → Packages](../reference/packages.md) — per-package install table.
- [Concepts → Execution loop](../concepts/execution-loop.md) — what `StatefulAiAgent.AskAsync` actually does.
