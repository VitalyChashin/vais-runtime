# Vais.Agents — documentation

Runtime for AI agents on .NET. Declarative manifests, Orleans-backed durability, multi-language plugins, swappable MAF/SK underneath. This tree is the full walkthrough — pick a [section](#sections) by audience or task; jump to [Concepts](#concepts) for the design model; open [Reference](#reference) for lookup tables.

> **Status.** Phase 1 — pre-alpha, pre-release. API unstable. NuGet not yet published.

## Sections

- **[Agent developer](agent-developer/index.md)** — build agents: declarative, observable through LLM and MCP gateways, in Python when YAML isn't enough, composed into multi-agent graphs.
- **[DevOps / admin](devops/index.md)** — run the runtime: Docker locally, Kubernetes in production, attach Redis or Postgres, wire Langfuse and Prometheus.
- **[Deep agent development](deep-development/index.md)** — author plugins: C# in-process, LangGraph Python, language-neutral container (Go worked example).
- **[Extensions](extensions/index.md)** — customize the runtime's seams: LLM and MCP gateway middleware, plus a catalog of the other extension points.
- **[Library mode](library-mode/index.md)** — embed `Vais.Agents` primitives in a .NET app without the runtime. Niche path.

## Concepts

One page per concept. Each explains what it is, the core types, how to wire it, extension points, and known limitations.

- [Architecture](concepts/architecture.md) — the 32 packages, layered diagram, dependency rules.
- [Declarative agents](concepts/declarative-agents.md) — manifest-driven instantiation; Model / SystemPromptSpec / Tools / Guardrails translation.
- [Runtime plugins](concepts/runtime-plugins.md) — code-authored `IAiAgent` DLLs loaded at silo startup; plugin-branch in the translator.
- [Polyglot plugins](concepts/polyglot-plugins.md) — Python MCP plugins spawned as subprocesses; tools contributed to the agent registry via `INamedToolSourceProvider`.
- [Session + memory](concepts/session.md) — `IAgentSession`, working vs session history, `IMemoryStore` scopes.
- [Context](concepts/context.md) — `IContextProvider` chain, `IContextWindowPacker`, merge rules.
- [Prompt](concepts/prompt.md) — `ISystemPromptComposer`, contributors, `IPromptTemplate`.
- [Guardrails](concepts/guardrails.md) — three-layer split (input / output / tool), Pass / Deny / Interrupt.
- [Execution loop](concepts/execution-loop.md) — `RunBudget`, `IToolCallDispatcher`, `AgentInterrupt`, tool-using streaming.
- [Tools](concepts/tools.md) — `ITool`, `IToolRegistry`, `IToolSource`, `Tool.FromFunc`, schema generation.
- [Orchestration](concepts/orchestration.md) — Sequential, RoundRobin, Handoff, `ITerminationCondition`.
- [Graph orchestration](concepts/graph-orchestration.md) — `IAgentGraph<TState>`, Pregel/BSP super-steps, node/edge/predicate model, checkpoint + resume (v0.9).
- [Control plane](concepts/control-plane.md) — `AgentManifest`, `IAgentLifecycleManager`, `IAgentRegistry`.
- [Kubernetes operator](concepts/kubernetes-operator.md) — `Agent` CRD, reconcile loop, phase state machine, projected SA-token auth (v0.13).
- [OPA policy engine](concepts/opa-policy-engine.md) — Rego-backed `IAgentPolicyEngine`, v1 input schema, FailMode semantics, decision cache (v0.14).
- [CLI](concepts/cli.md) — `vais` dotnet tool, subcommand map, kubectl-shape config, POSIX exit codes (v0.15).
- [Observability](concepts/observability.md) — OTel GenAI conventions, `vais.*` tags, Langfuse enrichment, event bus.
- [Persistence](concepts/persistence.md) — Orleans, Redis, Postgres, VectorData RAG.
- [Interop](concepts/interop.md) — MCP + A2A outbound adapters + MCP + A2A inbound servers (v0.7 + v0.8).

## Reference

- [Packages](reference/packages.md) — 27-package table with install guidance.
- [Events](reference/events.md) — `AgentEvent` + `AgentGraphEvent` closed hierarchies.
- [Budget](reference/budget.md) — `RunBudget` fields and enforcement points.
- [Graph predicate operators](reference/graph-predicate-operators.md) — ten-operator matcher vocabulary + combinators (v0.9).
- [Problem-details URNs](reference/problem-details-urns.md) — every `urn:vais-agents:*` the server emits + status + caller response (v0.11+).
- [Agent CRD](reference/agent-crd.md) — `vais.io/v1alpha1` schema, status fields, printer columns, reason vocabulary (v0.13).
- [CLI subcommands](reference/cli-subcommands.md) — per-command flag / argument / exit-code table for `vais` (v0.15).
- [CLI config file](reference/cli-config-file.md) — `~/.vais/config.yaml` schema + env-var overrides + token precedence (v0.15).
- [Runtime configuration](reference/runtime-configuration.md) — every env var + `appsettings.json` + Helm-values knob for the `vais-agents-runtime` container (v0.16).
- [Telemetry keys](reference/telemetry-keys.md) — `vais.*` OTel tags + Langfuse field mapping.
