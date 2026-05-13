# Vais.Agents — samples

Two flavors of sample, grouped by audience:

- **Runtime-first samples** — use the `vais-agents-runtime` container, declarative YAML manifests, and plugins. Aligned with the project's positioning; most users start here.
- **Library-mode samples** — embed `Vais.Agents` primitives in a .NET host. Useful when you want the agent classes without the runtime, or want a deeper view of the building blocks the runtime composes.

Most .NET samples are deterministic (scripted fake completion provider) and need no API key. Live-LLM and Orleans-persistence samples are flagged in the `API key` column. Python and Go plugin samples need no .NET build.

Run any .NET sample with `dotnet run --project samples/<Name>`. Run plugin samples by applying their `plugin.yaml` against the runtime via `vais apply -f`.

---

## Runtime-first samples

Start the runtime, declare agents, ship plugins. Each sample below pairs with one of the [Section 1–3 tutorials](../docs/index.md#sections).

| Sample | Feature | Packages | LoC | API key | Docs |
|---|---|---|---|---|---|
| [runtime-docker-compose](runtime-docker-compose) | start the runtime with docker-compose (localhost + clustered + OPA/OTel/Langfuse overlays) | — (config only) | 0 | — | [Deploy on Docker](../docs/devops/deploy-runtime-on-docker.md) |
| [declarative-agent-yaml](declarative-agent-yaml) | deploy a declarative agent via YAML manifest and `vais apply`; no C# required | — (YAML only) | 0 | `OPENAI_API_KEY` | [Your first declarative agent](../docs/agent-developer/your-first-declarative-agent.md) |
| [declarative-agent-mcp-gateways](declarative-agent-mcp-gateways) | declarative LLM + MCP gateway pipelines; zero C# — fallback, semantic cache, OTel, rate-limit, response truncation all via YAML | — (YAML only) | 0 | `OPENAI_API_KEY` | [Wire the LLM gateway](../docs/agent-developer/wire-the-llm-gateway.md), [Wire the MCP gateway](../docs/agent-developer/wire-the-mcp-gateway.md) |
| [graph-yaml-authored](graph-yaml-authored) | multi-agent graph as a YAML `AgentGraph` manifest (`vais apply`, `vais invoke-graph`, `--stream`) | — (YAML only) | 0 | `OPENAI_API_KEY` | [Compose a multi-agent graph](../docs/agent-developer/compose-a-multi-agent-graph.md) |
| [graph-cross-runtime](graph-cross-runtime) | graph with `ref.runtimeUrl` that fans a node out to a second runtime container | — (YAML only) | 0 | `OPENAI_API_KEY` | [cross-runtime-graphs](../docs/concepts/cross-runtime-graphs.md) |
| [quickstart-python-planner](quickstart-python-planner) | minimal Python container plugin — planner decomposing a query into sub-questions. IP-1 HTTP protocol with the `vais-plugin` SDK | — (Python) | ~30 | `OPENAI_API_KEY` (runtime env) | [Polyglot plugins](../docs/concepts/polyglot-plugins.md) |
| [quickstart-go-plugin](quickstart-go-plugin) | minimal Go container plugin — three IP-1 HTTP endpoints, stdlib only, ~10 MB binary on `scratch` | — (Go) | ~85 | — | [Author a container plugin in Go](../docs/deep-development/author-a-container-plugin-in-go.md) |
| [PluginAgentWeather](PluginAgentWeather) | code-authored agent packaged as a runtime plugin (`[VaisPlugin]`, overlay Dockerfile, `vais apply`/`vais invoke`) | Abstractions, Core | ~45 | — | [Author a C# plugin](../docs/deep-development/author-a-csharp-plugin.md) |
| [code-agent-plugin](code-agent-plugin) | code-authored `IAiAgent` plugin that injects `IHttpClientFactory` and calls OpenAI directly | Abstractions, Core | ~80 | `OPENAI_API_KEY` | [Author a C# plugin](../docs/deep-development/author-a-csharp-plugin.md) |
| [PluginAgentResearchPlanner](PluginAgentResearchPlanner) | Python plugin contributing tools (`decompose_task`, `score_plan_completeness`, `summarize_findings`) to a declarative agent via `transport: plugin` | — (Python + YAML) | ~120 | `ANTHROPIC_API_KEY` + `TAVILY_API_KEY` | [Polyglot plugins](../docs/concepts/polyglot-plugins.md) |
| [PluginAgentLangGraphResearcher](PluginAgentLangGraphResearcher) | hermetic Python agent-handler using a LangGraph-style two-node graph (plan→summarize + router); deterministic, CI-safe | — (Python + YAML) | ~120 | — | [Build a LangGraph plugin](../docs/deep-development/build-a-langgraph-plugin.md) |
| [PluginAgentLangGraphResearcherLive](PluginAgentLangGraphResearcherLive) | live-LLM Python agent-handler using real `langgraph.StateGraph` + `langchain-openai.ChatOpenAI`; same plan→summarize topology, real GPT-4o-mini calls | — (Python + YAML) | ~130 | `OPENAI_API_KEY` (runtime env) | [Build a LangGraph plugin](../docs/deep-development/build-a-langgraph-plugin.md) |
| [KubernetesOperatorQuickstart](KubernetesOperatorQuickstart) | `helm install` operator + apply `sample-agent.yaml` CR; watch reconcile → Ready; update spec; finalizer-based delete | — (YAML only) | 0 | — + K8s + Helm | [Deploy on Kubernetes](../docs/devops/deploy-runtime-on-kubernetes.md) |
| [CliCookbook](CliCookbook) | shell recipes: CI/CD apply with exit-code branching, rollback-on-failure, `vais logs` tailing + filtering, multi-context staging→prod promotion | — (scripts only) | 0 | — | [CLI](../docs/concepts/cli.md) |

### Runtime-first learning path

1. **runtime-docker-compose** — start the runtime with docker-compose.
2. **declarative-agent-yaml** — deploy your first declarative agent; `vais apply` and `vais invoke`.
3. **declarative-agent-mcp-gateways** — wire LLM + MCP gateway middleware via YAML.
4. **PluginAgentLangGraphResearcher** — drop into Python when YAML isn't enough.
5. **graph-yaml-authored** — compose multiple agents into a multi-agent graph.
6. **KubernetesOperatorQuickstart** — production deploy via Helm + `vais.io/v1alpha1` CRD.

For library mode (embed primitives in a .NET app instead), see the path below.

---

## Library-mode samples

Embed `StatefulAiAgent` and other primitives in your own .NET host. The first sample below is the canonical entry point — same code on Semantic Kernel and Microsoft Agent Framework.

| Sample | Feature | Packages | LoC | API key | Docs |
|---|---|---|---|---|---|
| [HelloAgent](HelloAgent) | session, tools, stack-neutral SK + MAF | Abstractions, Core, Ai.SemanticKernel, Ai.MicrosoftAgentFramework | ~145 | `OPENAI_API_KEY` | [30-second library hello](../docs/library-mode/hello-agent.md) |
| [PromptComposer](PromptComposer) | prompt composer + contributors | Abstractions, Core | ~70 | — | [prompt](../docs/concepts/prompt.md) |
| [CustomMemoryStore](CustomMemoryStore) | `IMemoryStore` file-backed impl | Abstractions, Core | ~95 | — | [session + memory](../docs/concepts/session.md) |
| [ContextProviderRag](ContextProviderRag) | `KnowledgeRetrievalContextProvider` w/ mock retriever | Abstractions, Core, Persistence.VectorData | ~75 | — | [context](../docs/concepts/context.md) |
| [InputOutputGuardrails](InputOutputGuardrails) | input + output guardrails | Abstractions, Core | ~90 | — | [guardrails](../docs/concepts/guardrails.md) |
| [ToolGuardrailsAndInterrupt](ToolGuardrailsAndInterrupt) | `IToolGuardrail` + HITL interrupt → resume | Abstractions, Core | ~130 | — | [guardrails](../docs/concepts/guardrails.md) |
| [BudgetEnforcement](BudgetEnforcement) | every `RunBudget` dimension trips | Abstractions, Core | ~115 | — | [budget](../docs/reference/budget.md) |
| [ToolFromFunc](ToolFromFunc) | `Tool.FromFunc<TIn,TOut>` + `IToolSource` | Abstractions, Core | ~75 | — | [tools](../docs/concepts/tools.md) |
| [AgentManifestAndRegistry](AgentManifestAndRegistry) | `AgentManifest` + `InMemoryAgentRegistry` | Abstractions, Core | ~70 | — | [control plane](../docs/concepts/control-plane.md) |
| [HelloStreaming](HelloStreaming) | basic `StreamAsync` with scripted deltas | Abstractions, Core | ~55 | — | [execution loop](../docs/concepts/execution-loop.md) |
| [HelloStreamingTools](HelloStreamingTools) | tool-using streaming | Abstractions, Core, Hosting.InMemory | ~100 | — | [stream-with-tools](../docs/guides/stream-with-tools.md) |
| [StreamingFilterTypingIndicator](StreamingFilterTypingIndicator) | `IStreamingAgentFilter` — `InvokeAsync` around-provider, `OnStreamDeltaAsync` per-delta, `OnStreamCompleteAsync` | Abstractions, Core | ~85 | — | [streaming-filters](../docs/concepts/streaming-filters.md) |
| [StreamingResiliencePolly](StreamingResiliencePolly) | `StatefulAgentOptions.StreamingResiliencePipeline` — Polly retry before first delta | Abstractions, Core, Microsoft.Extensions.Resilience | ~70 | — | [resilience](../docs/concepts/resilience.md) |
| [HttpStreamingInvoke](HttpStreamingInvoke) | `MapAgentControlPlane` SSE endpoint + `AgentControlPlaneClient.InvokeStreamEventsAsync` | Abstractions, Core, Hosting.InMemory, Control.InProcess, Control.Http.Server, Control.Http.Client | ~90 | — | [stream-invocations-over-http](../docs/guides/stream-invocations-over-http.md) |
| [HttpStreamingCancellation](HttpStreamingCancellation) | SSE cancellation — `CancellationTokenSource` fires mid-stream, server stops on `RequestAborted` | Abstractions, Core, Hosting.InMemory, Control.InProcess, Control.Http.Server, Control.Http.Client | ~85 | — | [stream-invocations-over-http](../docs/guides/stream-invocations-over-http.md) |
| [HttpIdempotencyInMemory](HttpIdempotencyInMemory) | `AddAgentControlPlaneIdempotency()` + `UseAgentControlPlaneIdempotency()`; same-key replay returns `Idempotency-Replayed: true`; agent invoked once | Abstractions, Core, Hosting.InMemory, Control.InProcess, Control.Http.Server, Control.Http.Client | ~85 | — | [idempotency](../docs/concepts/idempotency.md) |
| [OpenApiSpecExplorer](OpenApiSpecExplorer) | `AddAgentControlPlaneOpenApi()` + `MapAgentControlPlaneOpenApi()`; fetch `/openapi/v1.json`, print paths + `x-vais-type-urns` extension per error response | Abstractions, Core, Hosting.InMemory, Control.InProcess, Control.Http.Server | ~80 | — | [openapi-spec](../docs/guides/openapi-spec.md) |
| [OpaPolicyGateLocal](OpaPolicyGateLocal) | `AddOpaPolicyEngine()` + `LoggerAuditLog`; two `CreateAsync` calls — allowed provider passes, denied provider throws `AgentPolicyDeniedException`; audit entries logged; requires local `opa run --server` | Abstractions, Core, Hosting.InMemory, Control.InProcess, Control.Policy.Opa | ~85 | — + OPA | [policy](../docs/concepts/policy.md) |
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
| [McpServerStdio](McpServerStdio) | host an agent as an MCP tool over stdio; `AddMcpAgentServerStdio()` + `StdioAgentServerHost`; Claude Desktop config example | Abstractions, Core, Hosting.InMemory, Control.InProcess, Protocols.Mcp.Server | ~90 | — | [host-agents-as-mcp-tools](../docs/guides/host-agents-as-mcp-tools.md) |
| [McpServerHttp](McpServerHttp) | host an agent as an MCP tool over streamable-HTTP; `AddMcpAgentServerHttp()` + `MapMcpAgentServer()`; co-located `McpClient` exercises tools/list, tools/call, resources/list | Abstractions, Core, Hosting.InMemory, Control.InProcess, Protocols.Mcp.Server + ModelContextProtocol.Core | ~110 | — | [host-agents-as-mcp-tools](../docs/guides/host-agents-as-mcp-tools.md) |
| [A2AServerBasics](A2AServerBasics) | host an agent as an A2A endpoint; `AddA2AAgentServer()` + `MapA2AAgentServer()`; `AgentCard` auto-derived; co-located `A2AClient` exercises card discovery + message round-trip | Abstractions, Core, Hosting.InMemory, Control.InProcess, Protocols.A2A.Server + A2A + A2A.AspNetCore | ~100 | — | [host-agents-as-an-a2a-endpoint](../docs/guides/host-agents-as-an-a2a-endpoint.md) |
| [A2AInterruptResumeOrleans](A2AInterruptResumeOrleans) | `OrleansTaskStore` backing A2A task state; `IToolGuardrail` interrupt → `Task(InputRequired)` → resume via `message.TaskId` → `Task(Completed)` | Abstractions, Core, Hosting.InMemory, Control.InProcess, Protocols.A2A.Server, Hosting.Orleans + A2A + A2A.AspNetCore + Orleans | ~180 | — | [host-agents-as-an-a2a-endpoint](../docs/guides/host-agents-as-an-a2a-endpoint.md) |
| [AgentGraphInProcess](AgentGraphInProcess) | code-first `AgentGraphManifest` + `InProcessGraphOrchestrator<TState>`; `StreamAsync` events + `InvokeAsync`; typed `PipelineState` round-trip; `PropertyMatcher` conditional routing | Abstractions, Core, Hosting.InMemory, Control.InProcess | ~105 | — | [graph-orchestration](../docs/concepts/graph-orchestration.md) |
| [AgentGraphYamlLoader](AgentGraphYamlLoader) | `YamlAgentGraphManifestLoader.LoadFromFileAsync`; bag-state `InProcessGraphOrchestrator`; same triage graph defined in `triage-graph.yaml` | Abstractions, Core, Hosting.InMemory, Control.InProcess, Control.Manifests.Yaml | ~75 | — | [graph-orchestration](../docs/concepts/graph-orchestration.md) |
| [AgentGraphMaf](AgentGraphMaf) | `MafGraphOrchestrator` (MAF Workflows backend); proves cross-stack parity with `AgentGraphInProcess`; same manifest, same output, fan-out/fan-in unlocked | Abstractions, Core, Hosting.InMemory, Control.InProcess, Orchestration.Graph.MicrosoftAgentFramework | ~95 | — | [graph-orchestration](../docs/concepts/graph-orchestration.md) |
| [AgentGraphResumeOnOrleans](AgentGraphResumeOnOrleans) | `Interrupt`-kind node + `OrleansCheckpointer`; interrupt → `GraphInterrupted` → `LoadAsync` → `ResumeStreamAsync`; in-process Orleans silo, no Docker | Abstractions, Core, Hosting.InMemory, Control.InProcess, Hosting.Orleans + Orleans | ~145 | — | [graph-orchestration](../docs/concepts/graph-orchestration.md) |
| [graph-code-authored](graph-code-authored) | multi-agent graph authored in C# with `InProcessGraphOrchestrator` + typed state + streaming events | Abstractions, Core | ~70 | — | [graph-orchestration](../docs/concepts/graph-orchestration.md) |
| [GraphHitlLiveMode](GraphHitlLiveMode) | `IHitlAgentGraph<TState>.StreamWithHitlAsync` — live-mode HITL on `MafGraphOrchestrator`; inline handler approves (non-null) or aborts (null → `GraphHitlAbortedException`); contrast with halt-mode `AgentGraphResumeOnOrleans` | Abstractions, Core, Hosting.InMemory, Control.InProcess, Orchestration.Graph.MicrosoftAgentFramework | ~95 | — | [graph-orchestration](../docs/concepts/graph-orchestration.md) |
| [GraphPowerFxPredicates](GraphPowerFxPredicates) | `PowerFxGraphExpressionEvaluator` — inline `=Not(IsBlank(Local.research_plan))` PowerFx edge predicates in YAML manifest; `Vais.Agents.Core.PowerFx` package | Abstractions, Core, Hosting.InMemory, Control.InProcess, Control.Manifests.Yaml, Core.PowerFx | ~80 | — | [graph-orchestration](../docs/concepts/graph-orchestration.md) |
| [LlmGatewayMiddleware](LlmGatewayMiddleware) | `StatefulAgentOptions.GatewayMiddleware` — `LlmFallbackMiddleware` (pool fallback), `LlmSemanticCacheMiddleware` (same-text cache hit), `LlmJsonOutputMiddleware<T>` (JSON validation) | Abstractions, Core, Gateways.Fallback, Gateways.SemanticCache, Gateways.StructuredOutput | ~110 | — | [Author an LLM gateway middleware](../docs/extensions/author-an-llm-gateway-middleware.md) |
| [McpGatewayMiddleware](McpGatewayMiddleware) | `StatefulAgentOptions.ToolGatewayMiddleware` — `ToolRetryMiddleware` (retry on transient error), `ToolResultCacheMiddleware` (cache hit on identical args), `ToolArgumentValidationMiddleware` (ToolDenied on missing arg) | Abstractions, Core, Gateways.McpReliability, Gateways.McpCache, Gateways.McpSecurity | ~100 | — | [Author an MCP gateway middleware](../docs/extensions/author-an-mcp-gateway-middleware.md) |
| [OpenAiCompatGateway](OpenAiCompatGateway) | `AddOpenAiCompatGateway()` + `MapOpenAiCompat()` — expose a scripted `ICompletionProvider` as `POST /v1/chat/completions` + `GET /v1/models`; `AddInMemoryModelRouter` routes `gpt-4o-mini` alias; client calls non-streaming and SSE streaming paths | Abstractions, Core, Gateways.OpenAiCompat | ~130 | — | [openai-compat-gateway](../docs/guides/openai-compat-gateway.md) |

### Library-mode learning path

Walks the library primitives one at a time. Useful if you're embedding `Vais.Agents` in your own .NET host, or want a deeper understanding of the building blocks the runtime composes.

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
12. **McpServerStdio** → **McpServerHttp** — MCP server hosting.
13. **A2AServerBasics** → **A2AInterruptResumeOrleans** — A2A protocol.
14. **AgentGraphInProcess** → **AgentGraphYamlLoader** → **AgentGraphMaf** → **AgentGraphResumeOnOrleans** → **GraphHitlLiveMode** → **GraphPowerFxPredicates** — graph orchestration deep-dive: in-process, YAML, MAF, durable resume, live HITL, PowerFx predicates.
15. **LlmGatewayMiddleware** → **McpGatewayMiddleware** → **OpenAiCompatGateway** — gateway middleware authoring + OpenAI-compat surface.

For the runtime path (declarative YAML + `vais apply`), see the [runtime-first samples](#runtime-first-samples) above.

---

## Tooling-only samples

These directories contain no .NET code to compile — they are configuration, policy, or script artifacts that are read or applied by external tools:

| Directory | Contents | Tool |
|---|---|---|
| [opa-policies](opa-policies) | Rego policy files: model-provider allowlist, time-window gate, max-concurrent-runs cap | `opa run --server <policy.rego>` |
| [KubernetesOperatorQuickstart](KubernetesOperatorQuickstart) | `sample-agent.yaml` CRD + Helm walkthrough README | `kubectl apply` + `helm install` |
| [CliCookbook](CliCookbook) | Shell recipes + `~/.vais/config.yaml` starters | `vais` CLI |

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

Python plugin samples (`PluginAgent*`, `quickstart-python-planner`) and the Go plugin sample (`quickstart-go-plugin`) don't need a .NET build — register them via `vais apply -f samples/<Name>/plugin.yaml`.

Or run the [`build-all.ps1`](build-all.ps1) / [`build-all.sh`](build-all.sh) helper (supports `-RunDeterministic` / `RUN_DETERMINISTIC=1` to execute all deterministic samples in sequence).
