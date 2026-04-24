# Vais.Agents — samples

27 runnable samples. Each is a standalone .NET 9 console app or YAML-only configuration directory, consumes `Vais.Agents.*` via `PackageReference` against the local `artifacts/packages/` feed (see `NuGet.config`), and targets one scenario.

Run any sample with:

```bash
dotnet run --project samples/<Name>
```

Most samples are deterministic (scripted fake completion provider) and need no API key. The live-LLM sample (`HelloAgent`) gates on `OPENAI_API_KEY`. The Orleans persistence samples need Docker.

## Index

| Sample | Pillar / feature | Packages | LoC | API key | Docs |
|---|---|---|---|---|---|
| [HelloAgent](HelloAgent) | session, tools, stack-neutral SK + MAF | Abstractions, Core, Ai.SemanticKernel, Ai.MicrosoftAgentFramework | ~145 | `OPENAI_API_KEY` | [hello-agent](../docs/getting-started/hello-agent.md) |
| [PromptComposer](PromptComposer) | prompt composer + contributors | Abstractions, Core | ~70 | — | [prompt](../docs/concepts/prompt.md) |
| [CustomMemoryStore](CustomMemoryStore) | `IMemoryStore` file-backed impl | Abstractions, Core | ~95 | — | [session + memory](../docs/concepts/session.md) |
| [ContextProviderRag](ContextProviderRag) | `KnowledgeRetrievalContextProvider` w/ mock retriever | Abstractions, Core, Persistence.VectorData | ~75 | — | [context](../docs/concepts/context.md) |
| [InputOutputGuardrails](InputOutputGuardrails) | input + output guardrails | Abstractions, Core | ~90 | — | [guardrails](../docs/concepts/guardrails.md) |
| [ToolGuardrailsAndInterrupt](ToolGuardrailsAndInterrupt) | `IToolGuardrail` + HITL interrupt → resume | Abstractions, Core | ~130 | — | [guardrails](../docs/concepts/guardrails.md) |
| [BudgetEnforcement](BudgetEnforcement) | every `RunBudget` dimension trips | Abstractions, Core | ~115 | — | [budget](../docs/reference/budget.md) |
| [ToolFromFunc](ToolFromFunc) | `Tool.FromFunc<TIn,TOut>` + `IToolSource` | Abstractions, Core | ~75 | — | [tools](../docs/concepts/tools.md) |
| [AgentManifestAndRegistry](AgentManifestAndRegistry) | `AgentManifest` + `InMemoryAgentRegistry` | Abstractions, Core | ~70 | — | [control plane](../docs/concepts/control-plane.md) |
| [HelloStreaming](HelloStreaming) | basic `StreamAsync` with scripted deltas | Abstractions, Core | ~55 | — | [execution loop](../docs/concepts/execution-loop.md) |
| [HelloStreamingTools](HelloStreamingTools) | v0.4.1 tool-using streaming | Abstractions, Core, Hosting.InMemory | ~100 | — | [stream-with-tools](../docs/guides/stream-with-tools.md) |
| [SequentialOrchestration](SequentialOrchestration) | `SequentialOrchestrator` pipeline | Abstractions, Core | ~45 | — | [orchestration](../docs/concepts/orchestration.md) |
| [RoundRobinOrchestration](RoundRobinOrchestration) | `RoundRobinOrchestrator` + termination predicate | Abstractions, Core | ~45 | — | [orchestration](../docs/concepts/orchestration.md) |
| [HandoffBetweenAgents](HandoffBetweenAgents) | `Handoff` + `HandoffRequested` event | Abstractions, Core, Hosting.InMemory | ~65 | — | [orchestration](../docs/concepts/orchestration.md) |
| [OrleansSilo](OrleansSilo) | single-process Orleans silo + session | Abstractions, Core, Hosting.Orleans | ~75 | — | [run-on-orleans-locally](../docs/guides/run-on-orleans-locally.md) |
| [OrleansRedisPersistence](OrleansRedisPersistence) | Orleans backed by Redis | Abstractions, Core, Hosting.Orleans, Persistence.Redis | ~65 | — + Docker | [add-redis-persistence](../docs/guides/add-redis-persistence.md) |
| [OrleansPostgresPersistence](OrleansPostgresPersistence) | Orleans backed by Postgres | Abstractions, Core, Hosting.Orleans, Persistence.Postgres | ~65 | — + Docker | [add-postgres-persistence](../docs/guides/add-postgres-persistence.md) |
| [ObservabilityOtelConsole](ObservabilityOtelConsole) | OTel console exporter + Langfuse enrichment | Observability.* + OpenTelemetry.Exporter.Console | ~75 | — | [observability](../docs/concepts/observability.md) |
| [VectorDataRag](VectorDataRag) | VectorData-backed retriever end-to-end | Persistence.VectorData + SK InMemory + MEAI | ~120 | — | [wire-rag-via-vectordata](../docs/guides/wire-rag-via-vectordata.md) |
| [McpToolSourceExample](McpToolSourceExample) | `McpToolSource` wrapping shape | Protocols.Mcp + ModelContextProtocol.Core | ~55 | — (real MCP server optional) | [expose-mcp-tools-to-an-agent](../docs/guides/expose-mcp-tools-to-an-agent.md) |
| [A2ARemoteAgentExample](A2ARemoteAgentExample) | `A2ARemoteAgentTool` with stubbed `IA2AClient` | Protocols.A2A + A2A SDK | ~85 | — | [delegate-to-a2a-remote-agent](../docs/guides/delegate-to-a2a-remote-agent.md) |
| [PluginAgentWeather](PluginAgentWeather) | v0.18 code-authored agent packaged as a runtime plugin (`[VaisPlugin]`, overlay Dockerfile, `vais apply`/`vais invoke`) | Abstractions, Core | ~45 | — | [package-an-agent-as-a-plugin](../docs/guides/package-an-agent-as-a-plugin.md) |
| [PluginAgentResearchPlanner](PluginAgentResearchPlanner) | v0.23 Python plugin contributing tools (`decompose_task`, `score_plan_completeness`, `summarize_findings`) to a declarative agent via `transport: plugin` + `INamedToolSourceProvider` | — (Python + YAML) | ~120 | `ANTHROPIC_API_KEY` + `TAVILY_API_KEY` | [polyglot-plugins](../docs/concepts/polyglot-plugins.md), [package-a-python-plugin](../docs/guides/package-a-python-plugin.md) |
| [runtime-docker-compose](runtime-docker-compose) | v0.20 Phase 3 — start the runtime with docker-compose (localhost + clustered + OPA/OTel/Langfuse overlays) | — (config only) | 0 | — | [install-the-runtime](../docs/getting-started/install-the-runtime.md) |
| [declarative-agent-yaml](declarative-agent-yaml) | v0.20 Phase 3 — deploy a declarative agent via YAML manifest and `vais apply`; no C# required | — (YAML only) | 0 | `OPENAI_API_KEY` | [deploy-your-first-agent](../docs/getting-started/deploy-your-first-agent.md) |
| [code-agent-plugin](code-agent-plugin) | v0.20 Phase 3 — code-authored IAiAgent plugin that injects IHttpClientFactory and calls OpenAI directly | Abstractions, Core | ~80 | `OPENAI_API_KEY` | [package-an-agent-as-a-plugin](../docs/guides/package-an-agent-as-a-plugin.md) |
| [graph-code-authored](graph-code-authored) | v0.20 Phase 3 — multi-agent graph authored in C# with InProcessGraphOrchestrator + typed state + streaming events | Abstractions, Core | ~70 | — | [graph-orchestration](../docs/concepts/graph-orchestration.md) |
| [graph-yaml-authored](graph-yaml-authored) | v0.20 Phase 3 — multi-agent graph as a YAML AgentGraph manifest (`vais apply`, `vais invoke-graph`, `--stream`) | — (YAML only) | 0 | `OPENAI_API_KEY` | [from-zero-to-graph-in-20-minutes](../docs/tutorials/from-zero-to-graph-in-20-minutes.md) |
| [graph-cross-runtime](graph-cross-runtime) | v0.20 Phase 3 — graph with `ref.runtimeUrl` that fans a node out to a second runtime container | — (YAML only) | 0 | `OPENAI_API_KEY` | [cross-runtime-graphs](../docs/concepts/cross-runtime-graphs.md) |

## Suggested learning path

1. **HelloAgent** — the stack-neutral agent shape.
2. **ToolFromFunc** → **PromptComposer** → **InputOutputGuardrails** → **ToolGuardrailsAndInterrupt** — core pillars.
3. **CustomMemoryStore** → **ContextProviderRag** → **VectorDataRag** — memory + RAG.
4. **BudgetEnforcement** — safety rails.
5. **HelloStreaming** → **HelloStreamingTools** — streaming.
6. **SequentialOrchestration** → **RoundRobinOrchestration** → **HandoffBetweenAgents** — multi-agent.
7. **OrleansSilo** → **OrleansRedisPersistence** / **OrleansPostgresPersistence** — durable hosting.
8. **ObservabilityOtelConsole** — instrument everything.
9. **McpToolSourceExample** → **A2ARemoteAgentExample** — interop.
10. **AgentManifestAndRegistry** — control plane shape.
11. **runtime-docker-compose** → **declarative-agent-yaml** → **graph-yaml-authored** → **graph-cross-runtime** — Phase 3 runtime path.

## Build all

```bash
dotnet build samples/HelloAgent samples/PromptComposer samples/CustomMemoryStore samples/ContextProviderRag \
  samples/InputOutputGuardrails samples/ToolGuardrailsAndInterrupt samples/BudgetEnforcement \
  samples/ToolFromFunc samples/AgentManifestAndRegistry samples/HelloStreaming samples/HelloStreamingTools \
  samples/SequentialOrchestration samples/RoundRobinOrchestration samples/HandoffBetweenAgents \
  samples/OrleansSilo samples/OrleansRedisPersistence samples/OrleansPostgresPersistence \
  samples/ObservabilityOtelConsole samples/VectorDataRag samples/McpToolSourceExample samples/A2ARemoteAgentExample
```

Or run the [`build-all.ps1`](build-all.ps1) / [`build-all.sh`](build-all.sh) helper.
