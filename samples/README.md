# Vais.Agents — samples

52 runnable samples. Each is a standalone .NET 9 console app or YAML-only configuration directory, consumes `Vais.Agents.*` via `PackageReference` against the local `artifacts/packages/` feed (see `NuGet.config`), and targets one scenario.

Run any sample with:

```bash
dotnet run --project samples/<Name>
```

Most samples are deterministic (scripted fake completion provider) and need no API key. The live-LLM sample (`HelloAgent`) gates on `OPENAI_API_KEY`. The Orleans persistence samples need Docker. Two Python agent samples (`PluginAgentLangGraphResearcher` hermetic, `PluginAgentLangGraphResearcherLive` live-LLM) need no .NET build.

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
| [StreamingFilterTypingIndicator](StreamingFilterTypingIndicator) | v0.10 `IStreamingAgentFilter` — `InvokeAsync` around-provider, `OnStreamDeltaAsync` per-delta, `OnStreamCompleteAsync` | Abstractions, Core | ~85 | — | [streaming-filters](../docs/concepts/streaming-filters.md) |
| [StreamingResiliencePolly](StreamingResiliencePolly) | v0.10 `StatefulAgentOptions.StreamingResiliencePipeline` — Polly retry before first delta | Abstractions, Core, Microsoft.Extensions.Resilience | ~70 | — | [resilience](../docs/concepts/resilience.md) |
| [HttpStreamingInvoke](HttpStreamingInvoke) | v0.12 `MapAgentControlPlane` SSE endpoint + `AgentControlPlaneClient.InvokeStreamEventsAsync` | Abstractions, Core, Hosting.InMemory, Control.InProcess, Control.Http.Server, Control.Http.Client | ~90 | — | [stream-invocations-over-http](../docs/guides/stream-invocations-over-http.md) |
| [HttpStreamingCancellation](HttpStreamingCancellation) | v0.12 SSE cancellation — `CancellationTokenSource` fires mid-stream, server stops on `RequestAborted` | Abstractions, Core, Hosting.InMemory, Control.InProcess, Control.Http.Server, Control.Http.Client | ~85 | — | [stream-invocations-over-http](../docs/guides/stream-invocations-over-http.md) |
| [HttpIdempotencyInMemory](HttpIdempotencyInMemory) | v0.11 `AddAgentControlPlaneIdempotency()` + `UseAgentControlPlaneIdempotency()`; same-key replay returns `Idempotency-Replayed: true`; agent invoked once | Abstractions, Core, Hosting.InMemory, Control.InProcess, Control.Http.Server, Control.Http.Client | ~85 | — | [idempotency](../docs/concepts/idempotency.md) |
| [OpenApiSpecExplorer](OpenApiSpecExplorer) | v0.11 `AddAgentControlPlaneOpenApi()` + `MapAgentControlPlaneOpenApi()`; fetch `/openapi/v1.json`, print paths + `x-vais-type-urns` extension per error response | Abstractions, Core, Hosting.InMemory, Control.InProcess, Control.Http.Server | ~80 | — | [openapi-spec](../docs/guides/openapi-spec.md) |
| [OpaPolicyGateLocal](OpaPolicyGateLocal) | v0.14 `AddOpaPolicyEngine()` + `LoggerAuditLog`; two `CreateAsync` calls — allowed provider passes, denied provider throws `AgentPolicyDeniedException`; audit entries logged; requires local `opa run --server` | Abstractions, Core, Hosting.InMemory, Control.InProcess, Control.Policy.Opa | ~85 | — + OPA | [policy](../docs/concepts/policy.md) |
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
| [PluginAgentLangGraphResearcher](PluginAgentLangGraphResearcher) | v0.24 hermetic Python agent-handler using LangGraph-style two-node graph (plan→summarize + router); no real LLM calls — deterministic, CI-safe | — (Python + YAML) | ~120 | — | [polyglot-plugins](../docs/concepts/polyglot-plugins.md) |
| [PluginAgentLangGraphResearcherLive](PluginAgentLangGraphResearcherLive) | v0.24 live-LLM Python agent-handler using real `langgraph.StateGraph` + `langchain-openai.ChatOpenAI`; same plan→summarize topology, real GPT-4o-mini calls | — (Python + YAML) | ~130 | `OPENAI_API_KEY` (runtime host env) | [polyglot-plugins](../docs/concepts/polyglot-plugins.md) |
| [runtime-docker-compose](runtime-docker-compose) | v0.20 Phase 3 — start the runtime with docker-compose (localhost + clustered + OPA/OTel/Langfuse overlays) | — (config only) | 0 | — | [install-the-runtime](../docs/getting-started/install-the-runtime.md) |
| [declarative-agent-yaml](declarative-agent-yaml) | v0.20 Phase 3 — deploy a declarative agent via YAML manifest and `vais apply`; no C# required | — (YAML only) | 0 | `OPENAI_API_KEY` | [deploy-your-first-agent](../docs/getting-started/deploy-your-first-agent.md) |
| [code-agent-plugin](code-agent-plugin) | v0.20 Phase 3 — code-authored IAiAgent plugin that injects IHttpClientFactory and calls OpenAI directly | Abstractions, Core | ~80 | `OPENAI_API_KEY` | [package-an-agent-as-a-plugin](../docs/guides/package-an-agent-as-a-plugin.md) |
| [graph-code-authored](graph-code-authored) | v0.20 Phase 3 — multi-agent graph authored in C# with InProcessGraphOrchestrator + typed state + streaming events | Abstractions, Core | ~70 | — | [graph-orchestration](../docs/concepts/graph-orchestration.md) |
| [graph-yaml-authored](graph-yaml-authored) | v0.20 Phase 3 — multi-agent graph as a YAML AgentGraph manifest (`vais apply`, `vais invoke-graph`, `--stream`) | — (YAML only) | 0 | `OPENAI_API_KEY` | [from-zero-to-graph-in-20-minutes](../docs/tutorials/from-zero-to-graph-in-20-minutes.md) |
| [graph-cross-runtime](graph-cross-runtime) | v0.20 Phase 3 — graph with `ref.runtimeUrl` that fans a node out to a second runtime container | — (YAML only) | 0 | `OPENAI_API_KEY` | [cross-runtime-graphs](../docs/concepts/cross-runtime-graphs.md) |
| [declarative-agent-mcp-gateways](declarative-agent-mcp-gateways) | v0.40 — declarative LLM + MCP gateway pipelines; zero C# — LLM fallback, semantic cache, OTel, tool rate-limit, response truncation all via YAML manifests | — (YAML only) | 0 | `OPENAI_API_KEY` | [gateway-config-control-plane](../docs/concepts/gateway-config-control-plane.md) |
| [LlmGatewayMiddleware](LlmGatewayMiddleware) | v0.40 `StatefulAgentOptions.GatewayMiddleware` — `LlmFallbackMiddleware` (pool fallback), `LlmSemanticCacheMiddleware` (same-text cache hit), `LlmJsonOutputMiddleware<T>` (JSON validation); gateway packages `0.0.1-alpha` (pack first) | Abstractions, Core, Gateways.Fallback, Gateways.SemanticCache, Gateways.StructuredOutput | ~110 | — | [llm-gateway](../docs/concepts/llm-gateway.md) |
| [McpGatewayMiddleware](McpGatewayMiddleware) | v0.40 `StatefulAgentOptions.ToolGatewayMiddleware` — `ToolRetryMiddleware` (retry on transient error), `ToolResultCacheMiddleware` (cache hit on identical args), `ToolArgumentValidationMiddleware` (ToolDenied on missing arg); pipeline composed directly without agent loop | Abstractions, Core, Gateways.McpReliability, Gateways.McpCache, Gateways.McpSecurity | ~100 | — | [mcp-gateway](../docs/concepts/mcp-gateway.md) |
| [OpenAiCompatGateway](OpenAiCompatGateway) | v0.40 `AddOpenAiCompatGateway()` + `MapOpenAiCompat()` — expose a scripted `ICompletionProvider` as `POST /v1/chat/completions` + `GET /v1/models`; `AddInMemoryModelRouter` routes `gpt-4o-mini` alias; client calls non-streaming and SSE streaming paths; gateway package `0.0.1-alpha` (pack first) | Abstractions, Core, Gateways.OpenAiCompat | ~130 | — | [openai-compat-gateway](../docs/guides/openai-compat-gateway.md) |
| [GraphHitlLiveMode](GraphHitlLiveMode) | v0.42 `IHitlAgentGraph<TState>.StreamWithHitlAsync` — live-mode HITL on `MafGraphOrchestrator`; inline `Func<GraphInterrupted, CancellationToken, ValueTask<TState?>>` handler approves (non-null) or aborts (null → `GraphHitlAbortedException`); contrast with halt-mode `AgentGraphResumeOnOrleans` | Abstractions, Core, Hosting.InMemory, Control.InProcess, Orchestration.Graph.MicrosoftAgentFramework | ~95 | — | [graph-orchestration](../docs/concepts/graph-orchestration.md) |
| [GraphPowerFxPredicates](GraphPowerFxPredicates) | v0.53 `PowerFxGraphExpressionEvaluator` — inline `=Not(IsBlank(Local.research_plan))` PowerFx edge predicates in YAML manifest; two runs show both branches; `Vais.Agents.Core.PowerFx` package `0.15.0-preview` (pack first) | Abstractions, Core, Hosting.InMemory, Control.InProcess, Control.Manifests.Yaml, Core.PowerFx | ~80 | — | [graph-orchestration](../docs/concepts/graph-orchestration.md) |
| [KubernetesOperatorQuickstart](KubernetesOperatorQuickstart) | v0.13 — `helm install` operator + apply `sample-agent.yaml` CR; watch reconcile → Ready; update spec; finalizer-based delete | — (YAML only) | 0 | — + K8s + Helm | [kubernetes-operator](../docs/concepts/kubernetes-operator.md) |
| [CliCookbook](CliCookbook) | v0.15 — shell recipes: CI/CD apply with exit-code branching, rollback-on-failure, `vais logs` tailing + filtering, multi-context staging→prod promotion; three starter config files | — (scripts only) | 0 | — | [cli](../docs/concepts/cli.md) |
| [McpServerStdio](McpServerStdio) | v0.7 — host an agent as an MCP tool over stdio; `AddMcpAgentServerStdio()` + `StdioAgentServerHost`; Claude Desktop config example | Abstractions, Core, Hosting.InMemory, Control.InProcess, Protocols.Mcp.Server | ~90 | — | [host-agents-as-mcp-tools](../docs/guides/host-agents-as-mcp-tools.md) |
| [McpServerHttp](McpServerHttp) | v0.7 — host an agent as an MCP tool over streamable-HTTP; `AddMcpAgentServerHttp()` + `MapMcpAgentServer()`; co-located `McpClient` exercises tools/list, tools/call, resources/list | Abstractions, Core, Hosting.InMemory, Control.InProcess, Protocols.Mcp.Server + ModelContextProtocol.Core | ~110 | — | [host-agents-as-mcp-tools](../docs/guides/host-agents-as-mcp-tools.md) |
| [A2AServerBasics](A2AServerBasics) | v0.8 — host an agent as an A2A endpoint; `AddA2AAgentServer()` + `MapA2AAgentServer()`; `AgentCard` auto-derived; co-located `A2AClient` exercises card discovery + message round-trip | Abstractions, Core, Hosting.InMemory, Control.InProcess, Protocols.A2A.Server + A2A + A2A.AspNetCore | ~100 | — | [host-agents-as-an-a2a-endpoint](../docs/guides/host-agents-as-an-a2a-endpoint.md) |
| [A2AInterruptResumeOrleans](A2AInterruptResumeOrleans) | v0.8 — `OrleansTaskStore` backing A2A task state; `IToolGuardrail` interrupt → `Task(InputRequired)` → resume via `message.TaskId` → `Task(Completed)` | Abstractions, Core, Hosting.InMemory, Control.InProcess, Protocols.A2A.Server, Hosting.Orleans + A2A + A2A.AspNetCore + Orleans | ~180 | — | [host-agents-as-an-a2a-endpoint](../docs/guides/host-agents-as-an-a2a-endpoint.md) |
| [AgentGraphInProcess](AgentGraphInProcess) | v0.9 — code-first `AgentGraphManifest` + `InProcessGraphOrchestrator<TState>`; `StreamAsync` events + `InvokeAsync`; typed `PipelineState` round-trip; `PropertyMatcher` conditional routing | Abstractions, Core, Hosting.InMemory, Control.InProcess | ~105 | — | [graph-orchestration](../docs/concepts/graph-orchestration.md) |
| [AgentGraphYamlLoader](AgentGraphYamlLoader) | v0.9 — `YamlAgentGraphManifestLoader.LoadFromFileAsync`; bag-state `InProcessGraphOrchestrator`; same triage graph defined in `triage-graph.yaml` | Abstractions, Core, Hosting.InMemory, Control.InProcess, Control.Manifests.Yaml | ~75 | — | [graph-orchestration](../docs/concepts/graph-orchestration.md) |
| [AgentGraphMaf](AgentGraphMaf) | v0.9 — `MafGraphOrchestrator` (MAF Workflows backend); proves cross-stack parity with `AgentGraphInProcess`; same manifest, same output, fan-out/fan-in unlocked | Abstractions, Core, Hosting.InMemory, Control.InProcess, Orchestration.Graph.MicrosoftAgentFramework | ~95 | — | [graph-orchestration](../docs/concepts/graph-orchestration.md) |
| [AgentGraphResumeOnOrleans](AgentGraphResumeOnOrleans) | v0.9 — `Interrupt`-kind node + `OrleansCheckpointer`; interrupt → `GraphInterrupted` → `LoadAsync` → `ResumeStreamAsync`; in-process Orleans silo, no Docker | Abstractions, Core, Hosting.InMemory, Control.InProcess, Hosting.Orleans + Orleans | ~145 | — | [graph-orchestration](../docs/concepts/graph-orchestration.md) |

## Runtime-first learning path

The runtime + declarative + plugins path. Start here if you're aligned with the project's positioning — most users.

1. **runtime-docker-compose** — start the runtime with docker-compose.
2. **declarative-agent-yaml** — deploy your first declarative agent via YAML; `vais apply` and `vais invoke`.
3. **declarative-agent-mcp-gateways** — wire LLM + MCP gateway middleware chains via YAML. Logging, OTel, rate limit, response truncation, all declarative — zero C#.
4. **PluginAgentLangGraphResearcher** — drop into Python when YAML isn't enough. LangGraph plan→summarize topology.
5. **graph-yaml-authored** — compose multiple agents into a multi-agent graph; `vais invoke-graph --stream`.
6. **KubernetesOperatorQuickstart** — production deploy via Helm + `vais.io/v1alpha1` CRD.

For library mode (embed primitives in a .NET app instead of running the runtime), see the path below.

## Library-mode learning path

Walks the library primitives one at a time. Useful if you're embedding `Vais.Agents` in your own .NET host, or if you want a deeper understanding of the building blocks the runtime composes.

1. **HelloAgent** — the stack-neutral agent shape.
2. **ToolFromFunc** → **PromptComposer** → **InputOutputGuardrails** → **ToolGuardrailsAndInterrupt** — core pillars.
3. **CustomMemoryStore** → **ContextProviderRag** → **VectorDataRag** — memory + RAG.
4. **BudgetEnforcement** — safety rails.
5. **HelloStreaming** → **HelloStreamingTools** → **StreamingFilterTypingIndicator** → **StreamingResiliencePolly** — streaming + filters + resilience.
6. **HttpStreamingInvoke** → **HttpStreamingCancellation** → **HttpIdempotencyInMemory** → **OpenApiSpecExplorer** → **OpaPolicyGateLocal** — HTTP control plane surface + policy gate.
7. **SequentialOrchestration** → **RoundRobinOrchestration** → **HandoffBetweenAgents** — multi-agent.
8. **OrleansSilo** → **OrleansRedisPersistence** / **OrleansPostgresPersistence** — durable hosting.
9. **ObservabilityOtelConsole** — instrument everything.
10. **McpToolSourceExample** → **A2ARemoteAgentExample** — interop.
11. **AgentManifestAndRegistry** — control plane shape.
12. **runtime-docker-compose** → **declarative-agent-yaml** → **graph-yaml-authored** → **graph-cross-runtime** → **KubernetesOperatorQuickstart** — Phase 3 runtime path + Kubernetes operator.
13. **McpServerStdio** → **McpServerHttp** — MCP server hosting.
14. **A2AServerBasics** → **A2AInterruptResumeOrleans** — A2A protocol.
15. **AgentGraphInProcess** → **AgentGraphYamlLoader** → **AgentGraphMaf** → **AgentGraphResumeOnOrleans** → **GraphHitlLiveMode** → **GraphPowerFxPredicates** — graph orchestration deepdive: in-process, YAML, MAF, durable resume, live HITL, PowerFx predicates.
16. **CliCookbook** — CLI recipes: CI/CD apply, rollback, log tailing, multi-context deploy.

## Tooling-only samples

These directories contain no .NET code to compile — they are configuration, policy, or script artifacts that are read or applied by external tools:

| Directory | Contents | Tool |
|---|---|---|
| [opa-policies](opa-policies) | Rego policy files: model-provider allowlist, time-window gate, max-concurrent-runs cap | `opa run --server <policy.rego>` |
| [KubernetesOperatorQuickstart](KubernetesOperatorQuickstart) | `sample-agent.yaml` CRD + Helm walkthrough README | `kubectl apply` + `helm install` |
| [CliCookbook](CliCookbook) | Shell recipes (CI/CD apply, rollback, log tailing, multi-context deploy) + `~/.vais/config.yaml` starters | `vais` CLI |

## Build all

```bash
# deterministic samples (no API key, no Docker)
dotnet build samples/PromptComposer samples/CustomMemoryStore samples/ContextProviderRag \
  samples/InputOutputGuardrails samples/ToolGuardrailsAndInterrupt samples/BudgetEnforcement \
  samples/ToolFromFunc samples/AgentManifestAndRegistry samples/HelloStreaming samples/HelloStreamingTools \
  samples/SequentialOrchestration samples/RoundRobinOrchestration samples/HandoffBetweenAgents \
  samples/ObservabilityOtelConsole samples/VectorDataRag samples/McpToolSourceExample \
  samples/A2ARemoteAgentExample samples/McpServerStdio samples/McpServerHttp \
  samples/A2AServerBasics samples/AgentGraphInProcess samples/AgentGraphYamlLoader samples/AgentGraphMaf \
  samples/StreamingFilterTypingIndicator samples/StreamingResiliencePolly \
  samples/HttpIdempotencyInMemory samples/OpenApiSpecExplorer \
  samples/HttpStreamingInvoke samples/HttpStreamingCancellation \
  samples/LlmGatewayMiddleware samples/McpGatewayMiddleware samples/OpenAiCompatGateway \
  samples/GraphHitlLiveMode samples/GraphPowerFxPredicates

# in-process Orleans (no Docker)
dotnet build samples/A2AInterruptResumeOrleans samples/AgentGraphResumeOnOrleans

# Docker-backed Orleans
dotnet build samples/OrleansSilo samples/OrleansRedisPersistence samples/OrleansPostgresPersistence

# OPA required: opa run --server samples/OpaPolicyGateLocal/policy.rego
dotnet build samples/OpaPolicyGateLocal
```

Or run the [`build-all.ps1`](build-all.ps1) / [`build-all.sh`](build-all.sh) helper (supports `-RunDeterministic` / `RUN_DETERMINISTIC=1` to execute all deterministic samples in sequence).
