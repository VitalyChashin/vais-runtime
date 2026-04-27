# Vais.Agents — documentation

Stack-neutral agent library for .NET. This tree is the full walkthrough — concepts, guides, and reference. Start with **[Getting started](#getting-started)** if you're new; jump to **[Concepts](#concepts)** for the design story; open **[Reference](#reference)** for lookup tables.

> **Status.** Phase 1 — pre-alpha, pre-release. API unstable. NuGet not yet published.

## Getting started

- [Installation + package picks](getting-started/installation.md) — NuGet config, which package to install for which use case.
- [Hello agent](getting-started/hello-agent.md) — the 30-second walkthrough.
- [Install the CLI](getting-started/install-the-cli.md) — `dotnet tool install -g Vais.Agents.Cli` + first-run config (v0.15).
- [Install the runtime locally](guides/install-the-runtime-locally.md) — docker-compose recipes for the `vais-agents-runtime` container (v0.16).
- [Deploy the runtime to Kubernetes](guides/deploy-the-runtime-to-kubernetes.md) — Helm chart walkthrough: kind quickstart → production shape (v0.16).
- [Choosing a stack: SK vs MAF](getting-started/choosing-a-stack.md) — decision framework + the parity findings that shaped the library.

## Concepts

One page per pillar. Each explains what it is, the core types, how to wire it, extension points, and known limitations.

- [Architecture](concepts/architecture.md) — the 32 packages, layered diagram, dependency rules.
- [Declarative agents](concepts/declarative-agents.md) — manifest-driven instantiation (v0.17 Pillar B); Model / SystemPromptSpec / Tools / Guardrails translation.
- [Runtime plugins](concepts/runtime-plugins.md) — code-authored `IAiAgent` DLLs loaded at silo startup; plugin-branch in the translator (v0.18 Pillar C).
- [Polyglot plugins](concepts/polyglot-plugins.md) — Python MCP plugins spawned as subprocesses; tools contributed to the agent registry via `INamedToolSourceProvider` (v0.23).
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

## Guides

Task-focused, sample-backed recipes.

- [Wire a custom tool](guides/wire-a-custom-tool.md)
- [Add input and output guardrails](guides/add-input-output-guardrails.md)
- [Plug in gateway middleware](guides/plug-in-gateway-middleware.md) — fallback, caching, rate limiting, structured output, test doubles.
- [Run on Orleans locally](guides/run-on-orleans-locally.md)
- [Add Redis persistence](guides/add-redis-persistence.md)
- [Add Postgres persistence](guides/add-postgres-persistence.md)
- [Wire RAG via VectorData](guides/wire-rag-via-vectordata.md)
- [Stream with tools](guides/stream-with-tools.md)
- [Expose MCP tools to an agent](guides/expose-mcp-tools-to-an-agent.md) — outbound MCP.
- [Delegate to an A2A remote agent](guides/delegate-to-a2a-remote-agent.md) — outbound A2A.
- [Host agents as MCP tools](guides/host-agents-as-mcp-tools.md) — inbound MCP (v0.7).
- [Host agents as A2A endpoints](guides/host-agents-as-a2a-endpoints.md) — inbound A2A (v0.8).
- [Compose an agent graph (YAML)](guides/compose-an-agent-graph-yaml.md) — `kind: AgentGraph` manifests + `InProcessGraphOrchestrator` (v0.9).
- [Run resumable graphs on Orleans](guides/run-resumable-graphs-on-orleans.md) — `OrleansCheckpointer` + durable HITL interrupts (v0.9).
- [Enable HTTP idempotency](guides/enable-http-idempotency.md) — `Idempotency-Key` + Stripe-shape middleware (v0.11).
- [Consume the OpenAPI spec](guides/consume-the-openapi-spec.md) — `/openapi/v1.json` + URN codegen (v0.11).
- [Stream invocations over HTTP](guides/stream-invocations-over-http.md) — SSE streaming route + `AgentEvent` wire taxonomy (v0.12).
- [Deploy the Kubernetes operator](guides/deploy-the-kubernetes-operator.md) — Docker Desktop quick-start, `Agent` CR lifecycle (v0.13).
- [Wire a sidecar OPA against the operator](guides/wire-a-sidecar-opa-against-the-operator.md) — combined v0.13 + v0.14 policy deployment.
- [Gate agents with OPA](guides/gate-agents-with-opa.md) — run OPA locally, register the engine, observe denials (v0.14).
- [Author a Rego policy against the VAIS input schema](guides/author-a-rego-policy-against-the-vais-input-schema.md) — four guard patterns (v0.14).
- [Apply manifests from CI](guides/apply-manifests-from-ci.md) — `vais apply -f` + exit-code handling in scripts (v0.15).
- [Tail live runs with `vais logs`](guides/tail-live-runs-with-vais-logs.md) — SSE attach + client-side filters (v0.15).
- [Install the runtime locally](guides/install-the-runtime-locally.md) — docker-compose recipes: localhost + clustered + OPA/Langfuse/OTel overlays (v0.16).
- [Deploy the runtime to Kubernetes](guides/deploy-the-runtime-to-kubernetes.md) — Helm install from kind to production with external Redis (v0.16).
- [Author an agent in YAML](guides/author-an-agent-in-yaml.md) — pure-YAML declarative agent, no consumer C# (v0.17).
- [Package an agent as a plugin](guides/package-an-agent-as-a-plugin.md) — code-authored `IAiAgent` DLL + overlay image + `vais apply`/`invoke` (v0.18).
- [Package a Python plugin](guides/package-a-python-plugin.md) — FastMCP stdio server + `plugin.yaml` + overlay image + declarative agent (v0.23).
- [Deploy OTel and Langfuse](guides/deploy-otel-and-langfuse.md)

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

## Architecture decisions

- [ADR index](adr/index.md)

## Package-to-pillar quick map

| If you want to… | Install |
|---|---|
| Ship a single-process agent that talks to OpenAI | `Vais.Agents.Core` + `Vais.Agents.Ai.SemanticKernel` (or `…MicrosoftAgentFramework`) |
| Host agents across a cluster with durable state | Same + `Vais.Agents.Hosting.Orleans` + `Vais.Agents.Persistence.Redis` (or `…Postgres`) |
| Export traces + metrics to OTel | Same + `Vais.Agents.Observability.OpenTelemetry` |
| Surface to Langfuse specifically | Same + `Vais.Agents.Observability.Langfuse` |
| RAG over a vector store | Same + `Vais.Agents.Persistence.VectorData` |
| Pull tools from an MCP server | Same + `Vais.Agents.Protocols.Mcp` |
| Delegate subtasks to a remote A2A agent | Same + `Vais.Agents.Protocols.A2A` |
| Expose agents as MCP tools (stdio or HTTP) | Same + `Vais.Agents.Protocols.Mcp.Server` |
| Expose agents as A2A endpoints | Same + `Vais.Agents.Protocols.A2A.Server` (+ `AddOrleansA2ATaskStore` for durable `input-required`) |
| Orchestrate agents on a state-threaded graph | Same + `Vais.Agents.Core` (`InProcessGraphOrchestrator`) + `Vais.Agents.Control.Manifests.Yaml` for YAML graphs (+ `AddOrleansGraphCheckpointer` for durable resume) |
| Run the graph on MAF Workflows instead | Same + `Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework` |
| Host the HTTP control plane with idempotency + OpenAPI | Same + `Vais.Agents.Control.Http.Server` (+ `AddOrleansIdempotencyStore` for durable deduplication) |
| Stream invocations as SSE over HTTP | Same + `Vais.Agents.Control.Http.Server` + `Vais.Agents.Control.Http.Client` |
| Deploy agents declaratively from Kubernetes | HTTP control plane + `Vais.Agents.Control.KubernetesOperator` + the Helm chart in `deploy/helm/vais-agents-operator/` |
| Gate every agent verb through a Rego policy | Same + `Vais.Agents.Control.Policy.Opa` + an OPA sidecar / standalone server |
| Operate the control plane from a shell | `dotnet tool install -g Vais.Agents.Cli` — bundles the `vais` command on PATH |
| Ship the runtime as a container image | `docker build -f src/Vais.Agents.Runtime.Host/Dockerfile .` — v0.16 Pillar A; Helm chart in `deploy/helm/vais-agents-runtime/` for Kubernetes, docker-compose recipes in `deploy/compose/` for local dev |
| Package a code-authored agent as a loadable plugin | `Vais.Agents.Abstractions` + `Vais.Agents.Core` + `[assembly: VaisPlugin]` in a separate `classlib` publish + overlay Dockerfile over `vais-agents-runtime:0.18.0-preview` (v0.18 Pillar C) |
