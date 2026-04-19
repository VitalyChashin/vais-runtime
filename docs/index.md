# Vais.Agents — documentation

Stack-neutral agent library for .NET. This tree is the full walkthrough — concepts, guides, and reference. Start with **[Getting started](#getting-started)** if you're new; jump to **[Concepts](#concepts)** for the design story; open **[Reference](#reference)** for lookup tables.

> **Status.** Phase 1 — pre-alpha, pre-release. API unstable. NuGet not yet published.

## Getting started

- [Installation + package picks](getting-started/installation.md) — NuGet config, which package to install for which use case.
- [Hello agent](getting-started/hello-agent.md) — the 30-second walkthrough.
- [Choosing a stack: SK vs MAF](getting-started/choosing-a-stack.md) — decision framework + the parity findings that shaped the library.

## Concepts

One page per pillar. Each explains what it is, the core types, how to wire it, extension points, and known limitations.

- [Architecture](concepts/architecture.md) — the 13 packages, layered diagram, dependency rules.
- [Session + memory](concepts/session.md) — `IAgentSession`, working vs session history, `IMemoryStore` scopes.
- [Context](concepts/context.md) — `IContextProvider` chain, `IContextWindowPacker`, merge rules.
- [Prompt](concepts/prompt.md) — `ISystemPromptComposer`, contributors, `IPromptTemplate`.
- [Guardrails](concepts/guardrails.md) — three-layer split (input / output / tool), Pass / Deny / Interrupt.
- [Execution loop](concepts/execution-loop.md) — `RunBudget`, `IToolCallDispatcher`, `AgentInterrupt`, tool-using streaming.
- [Tools](concepts/tools.md) — `ITool`, `IToolRegistry`, `IToolSource`, `Tool.FromFunc`, schema generation.
- [Orchestration](concepts/orchestration.md) — Sequential, RoundRobin, Handoff, `ITerminationCondition`.
- [Control plane](concepts/control-plane.md) — `AgentManifest`, `IAgentLifecycleManager`, `IAgentRegistry`.
- [Observability](concepts/observability.md) — OTel GenAI conventions, `vais.*` tags, Langfuse enrichment, event bus.
- [Persistence](concepts/persistence.md) — Orleans, Redis, Postgres, VectorData RAG.
- [Interop](concepts/interop.md) — MCP + A2A outbound adapters.

## Guides

Task-focused, sample-backed recipes.

- [Wire a custom tool](guides/wire-a-custom-tool.md)
- [Add input and output guardrails](guides/add-input-output-guardrails.md)
- [Run on Orleans locally](guides/run-on-orleans-locally.md)
- [Add Redis persistence](guides/add-redis-persistence.md)
- [Add Postgres persistence](guides/add-postgres-persistence.md)
- [Wire RAG via VectorData](guides/wire-rag-via-vectordata.md)
- [Stream with tools](guides/stream-with-tools.md)
- [Expose MCP tools to an agent](guides/expose-mcp-tools-to-an-agent.md)
- [Delegate to an A2A remote agent](guides/delegate-to-a2a-remote-agent.md)
- [Deploy OTel and Langfuse](guides/deploy-otel-and-langfuse.md)

## Reference

- [Packages](reference/packages.md) — 13-package table with install guidance.
- [Events](reference/events.md) — `AgentEvent` closed hierarchy.
- [Budget](reference/budget.md) — `RunBudget` fields and enforcement points.
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
