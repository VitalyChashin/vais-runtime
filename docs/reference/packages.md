# Reference: packages

**56 projects** under `src/` — 54 ship as NuGet (52 libraries + `Vais.Agents.Cli` dotnet tool + `Vais.Plugin.Sdk` container plugin SDK) and 2 are **in-repo composition / host projects** (`Vais.Agents.Runtime.Host` and `Vais.Agents.Control.KubernetesOperator.Host`, both `IsPackable=false`, both shipping as container images rather than NuGet packages). Target framework: `net9.0`. Versions track preview tags — not yet published to nuget.org.

Sections below cover each tier. The runtime container is documented separately at [Runtime container](#runtime-container-v016); the K8s operator host at [Kubernetes-native deployment](#control-plane--kubernetes-v013).

## Contracts

| Package | Purpose | Key entry points | Install when… |
|---|---|---|---|
| `Vais.Agents.Abstractions` | Neutral agent + provider + memory + context + tool + event contracts. No SK / MAF / Orleans / AspNetCore deps. | `ICompletionProvider`, `IAiAgent`, `IStreamingAiAgent`, `IAgentGraph<TState>`, `AgentEvent` (12 subclasses), `AgentGraphEvent` (10 subclasses), `AgentManifest`, `RunBudget`, `ITool`, `AgentInputMiddleware`, `IAgentInputMiddlewareFactory`, `IContainerPluginRegistry`, `LocalAgentRef` | Authoring a custom adapter or host. Otherwise pulled transitively. |
| `Vais.Agents.Control.Abstractions` | Control-plane verb contracts + idempotency / policy / audit / secret interfaces. | `IAgentLifecycleManager`, `IAgentPolicyEngine`, `IIdempotencyStore`, `IAgentAuditLog`, `IAgentSecretResolver`, `PolicyDecision`, `PolicyOperation` | Authoring a custom control plane or policy engine. Otherwise pulled transitively. |

## Core

| Package | Purpose | Key entry points | Install when… |
|---|---|---|---|
| `Vais.Agents.Core` | Default `StatefulAiAgent` + execution loop + in-process defaults + diagnostics constants + `InProcessGraphOrchestrator` (zero-MAF-dep) + `InMemoryCheckpointer`. | `StatefulAiAgent`, `StatefulAgentOptions`, `DefaultToolCallDispatcher`, `InProcessGraphOrchestrator<TState>`, `AgenticDiagnostics`, `AgenticTags` | Any scenario that builds or runs an agent. |
| `Vais.Agents.Core.PowerFx` | PowerFx expression evaluator for inline `=...` edge predicates in graph manifests. Depends on `Vais.Agents.Core` + `Microsoft.PowerFx.Interpreter`. State keys exposed under `Local.*`; `Local.lastMessage` shortcut for the last messages-array entry. | `PowerFxGraphExpressionEvaluator`, `AddPowerFxExpressionEvaluator` | Any graph that uses `when: "=<expr>"` PowerFx predicates. The runtime container wires this automatically. |

## Gateway plugins

Optional middleware packages that plug into any `StatefulAiAgent` via `StatefulAgentOptions.GatewayMiddleware`. Each package depends only on `Vais.Agents.Abstractions` and can be used standalone or in combination. Middleware is applied outermost-first (index 0 = first to intercept).

| Package | Purpose | Key entry points | Install when… |
|---|---|---|---|
| `Vais.Agents.Gateways.Fallback` | Provider fallback and load balancing. `LlmFallbackMiddleware` tries providers in sequence, skipping failed ones. `LlmLoadBalancingMiddleware` distributes calls round-robin. Streaming fallback commits on the first successful delta. | `LlmFallbackMiddleware`, `LlmLoadBalancingMiddleware`, `IFallbackProviderPool`, `InMemoryFallbackProviderPool`, `AddLlmFallbackMiddleware`, `AddLlmLoadBalancingMiddleware` | You run multiple LLM providers and want automatic failover or load distribution. |
| `Vais.Agents.Gateways.SemanticCache` | Response caching. Short-circuits the LLM call when a matching entry is found; populates the cache on miss. Streaming path drains and caches on miss; hit yields a single synthetic delta. Default key: last user turn text. | `LlmSemanticCacheMiddleware`, `ISemanticCacheStore`, `InMemorySemanticCacheStore`, `AddLlmSemanticCacheMiddleware` | You have repeated or near-identical prompts and want to reduce latency and cost. Swap `InMemorySemanticCacheStore` for a vector-similarity store to extend beyond exact-match. |
| `Vais.Agents.Gateways.Governance` | Sliding-window rate limiting. Enforces per-key request-count and token-count budgets; throws `AgentBudgetExceededException` on breach. `InMemorySlidingWindowRateLimitStore` accepts `TimeProvider` for testability. | `LlmRateLimitMiddleware`, `RateLimitOptions`, `IRateLimitStore`, `InMemorySlidingWindowRateLimitStore`, `AddLlmRateLimitMiddleware` | You need per-user / per-workspace / per-tenant call budgets enforced in-process. Implement `IRateLimitStore` over Redis for distributed enforcement. |
| `Vais.Agents.Gateways.StructuredOutput` | JSON output validation. Deserializes each response as `T` via `System.Text.Json`; throws `AgentGuardrailDeniedException(GuardrailLayer.Output)` on failure. Covers both the non-streaming turn and the streaming completion via `OnStreamCompleteAsync`. | `LlmJsonOutputMiddleware<T>` | Your agent must produce machine-readable JSON and you want a hard guardrail that surfaces parse failures before the response reaches the caller. |
| `Vais.Agents.Gateways.Testing` | Test-double middleware. `LlmMockMiddleware` queues `CompletionResponse` objects and dequeues one per call (both paths), bypassing the real provider entirely. Throws `InvalidOperationException` when the queue is exhausted. | `LlmMockMiddleware` | Unit-testing agent logic — tool routing, multi-turn history, budget enforcement — without a real LLM. |
| `Vais.Agents.Gateways.OpenAiCompat` | OpenAI-compatible HTTP gateway. Exposes `POST /v1/chat/completions` (non-streaming JSON + streaming SSE) and `GET /v1/models` over any `IModelRouter` + `LlmGatewayMiddleware` chain. Bearer auth via `IInboundIdentityResolver`. Error mapping: 401/404/422/429/400/500. All DTOs are `internal`. | `AddOpenAiCompatGateway`, `MapOpenAiCompat`, `IInboundIdentityResolver`, `IModelRouter`, `ModelRoute`, `ModelNotFoundException`, `PassThroughIdentityResolver`, `InMemoryModelRouter`, `LlmGatewayPipeline` | Exposing your gateway as an OpenAI-compatible endpoint so Python clients, LiteLLM, or any OpenAI-SDK consumer can target it without custom integration. |
| `Vais.Agents.Gateways.Prometheus` | Prometheus metrics middleware. `LlmPrometheusMiddleware` records `llm_requests_total`, `llm_request_duration_seconds`, and `llm_tokens_total` per call, labelled by `model` and `workspace`. Depends on `Abstractions` + `prometheus-net` only (no `Core` dep). Test constructor accepts an isolated `MetricFactory`. | `LlmPrometheusMiddleware`, `AddLlmPrometheusMiddleware` | Scraping LLM call metrics into any Prometheus-compatible backend (Grafana, Thanos, etc.). |

## MCP/tool gateway plugins

Optional middleware packages that plug into any `StatefulAiAgent` via `StatefulAgentOptions.ToolGatewayMiddleware`. These intercept tool invocations (not LLM calls) and compose using the same right-to-left ordering as LLM gateway middleware. Each package depends only on `Vais.Agents.Abstractions`.

| Package | Purpose | Key entry points | Install when… |
|---|---|---|---|
| `Vais.Agents.Gateways.McpReliability` | Reliability primitives for tool calls. `ToolRetryMiddleware` retries failed calls with exponential backoff, skipping non-retryable errors (`ToolDenied`, `CircuitOpen`, `ToolRateLimitExceeded`). `ToolTimeoutGuard` enforces a per-dispatch deadline, returning `ToolTimeout` instead of throwing. `ToolCircuitBreakerMiddleware` tracks per-workspace failures and opens the circuit after a threshold, returning `CircuitOpen` during the open window. | `ToolRetryMiddleware`, `ToolTimeoutGuard`, `ToolCircuitBreakerMiddleware`, `AddToolRetryMiddleware`, `AddToolTimeoutMiddleware`, `AddToolCircuitBreakerMiddleware` | Your agent calls external MCP servers or unreliable tools and needs retry, deadline, or circuit-breaker protection without changing tool implementations. |
| `Vais.Agents.Gateways.McpCache` | Deterministic result cache for tool calls. `ToolResultCacheMiddleware` short-circuits repeated identical calls; cache key is `{toolName}:{arguments}` (normalized JSON). Only success outcomes are stored. Excluded tools (side-effectful) always bypass the cache. `InMemoryToolResultCache` (thread-safe `ConcurrentDictionary`). `IToolResultCache` seam for distributed stores. | `ToolResultCacheMiddleware`, `IToolResultCache`, `InMemoryToolResultCache`, `AddInMemoryToolResultCache`, `AddToolResultCacheMiddleware` | You have deterministic tools called repeatedly with the same arguments (search, lookup, read-only APIs) and want to cut redundant invocations. |
| `Vais.Agents.Gateways.McpGovernance` | Per-workspace rate limiting and tool access policy. `ToolRateLimitMiddleware` enforces sliding-window per-workspace-per-tool request budgets via `IRateLimitStore` (from `Vais.Agents.Gateways.Governance`); returns `ToolRateLimitExceeded` on breach. `ToolWorkspacePolicyMiddleware` evaluates a `WorkspaceToolPolicy` per workspace: prefix-based allow/deny lists plus a minimum `PrivilegeLevel` gate (`Platform=0` highest, `Agent=2` lowest). | `ToolRateLimitMiddleware`, `ToolRateLimitOptions`, `ToolWorkspacePolicyMiddleware`, `WorkspaceToolPolicy`, `AddToolRateLimitMiddleware`, `AddToolWorkspacePolicyMiddleware` | Multi-tenant deployments that need per-workspace tool budgets or declarative allow/deny rules without modifying tool code. |
| `Vais.Agents.Gateways.McpSecurity` | Input validation and output size guardrails. `ToolArgumentValidationMiddleware` checks that declared required fields are present in `Arguments`, returning `ToolDenied` (with the missing field list in `Result`) without calling the tool. `ToolOutputLengthGuard` rejects (not truncates) oversized responses with `ToolOutputTooLarge`. | `ToolArgumentValidationMiddleware`, `ToolOutputLengthGuard`, `AddToolArgumentValidationMiddleware`, `AddToolOutputLengthGuard` | You need to harden tool dispatch against malformed model-generated calls, or enforce a hard cap on response size before it enters the context window. |
| `Vais.Agents.Gateways.McpTransformation` | Response normalisation. `ToolJsonRepairMiddleware` validates JSON responses and attempts structural repair (stub — safe no-op on failure). `ToolHtmlToMarkdownMiddleware` detects HTML responses by heuristic, strips tags via regex, and decodes HTML entities so the model sees clean plain text. Both middlewares pass error outcomes through unchanged. | `ToolJsonRepairMiddleware`, `ToolHtmlToMarkdownMiddleware`, `AddToolJsonRepairMiddleware`, `AddToolHtmlToMarkdownMiddleware` | Your MCP servers return HTML (e.g. web scraping tools) or may return malformed JSON, and you want to normalise before the result re-enters the context window. |

## Adapters

| Package | Purpose | Key entry points | Install when… |
|---|---|---|---|
| `Vais.Agents.Ai.SemanticKernel` | `SkCompletionProvider` over SK's `IChatCompletionService`. | `SkCompletionProvider` | Your app has a `Kernel` and you want agents on SK. |
| `Vais.Agents.Ai.MicrosoftAgentFramework` | `MafCompletionProvider` over MAF's `ChatClientAgent` + MEAI's `IChatClient`. | `MafCompletionProvider` | Your app uses MEAI / MAF and you want agents on that stack. |

## Hosting

| Package | Purpose | Key entry points | Install when… |
|---|---|---|---|
| `Vais.Agents.Hosting.InMemory` | `InMemoryAgentRuntime` + `InMemoryAgentEventBus` + `InMemoryAgentRegistry`. One process, no cluster. | `AddAgenticInMemoryHosting`, `InMemoryAgentEventBus` | Dev, CLI tools, tests. |
| `Vais.Agents.Hosting.Orleans` | `OrleansAgentRuntime` + `AiAgentGrain` + `AgentSessionGrain` + `AgentConfigGrain` + `OrleansAgentEventBus` + durability sidecars (`OrleansTaskStore` for v0.8 A2A, `OrleansCheckpointer` for v0.9 graph, `OrleansIdempotencyStore` for v0.11 HTTP, `OrleansAgentRegistry` for v0.17 manifest persistence). | `AddOrleansAgentRuntime`, `AddOrleansAgentEventBus`, `ConfigureAgentGrains`, `AddOrleansA2ATaskStore`, `AddOrleansGraphCheckpointer`, `AddOrleansIdempotencyStore`, `AddOrleansAgentRegistry` | Multi-process or clustered deployments needing durable state. |

## Persistence

| Package | Purpose | Key entry points | Install when… |
|---|---|---|---|
| `Vais.Agents.Persistence.Redis` | Orleans clustering + grain storage + streams on Redis. | `UseAgenticRedisClustering`, `AddAgenticRedisGrainStorage`, `UseAgenticRedisStreaming` | Running Orleans with Redis for membership / grain storage / streams. |
| `Vais.Agents.Persistence.Postgres` | Orleans clustering + grain storage on Postgres (ADO.NET). No streams provider (alpha-only upstream). | `UseAgenticPostgresClustering`, `AddAgenticPostgresGrainStorage` | Running Orleans with Postgres for membership / grain storage. |
| `Vais.Agents.Persistence.VectorData` | `VectorStoreKnowledgeRetriever<TKey, TRecord>` + `KnowledgeRetrievalContextProvider`. | `VectorStoreKnowledgeRetriever<TKey, TRecord>`, `KnowledgeRetrievalContextProvider` | RAG — augmenting prompts with retrieved chunks from any MEAI VectorData collection. |

## Observability

| Package | Purpose | Key entry points | Install when… |
|---|---|---|---|
| `Vais.Agents.Observability.OpenTelemetry` | `OpenTelemetryUsageSink` + `AddAgenticInstrumentation` extensions for Tracer / Meter providers. Registers five `ActivitySource`s: `Vais.Agents`, `Vais.Agents.Hosting.Orleans`, `Vais.Agents.Core.Graph`, `Vais.Agents.Runtime.Plugins.Python`, `Vais.Agents.Runtime.Plugins.Container.Otlp`. | `OpenTelemetryUsageSink`, `AddAgenticInstrumentation` | Exporting traces + metrics to any OTel collector (Jaeger, Tempo, Datadog, Grafana). |
| `Vais.Agents.Observability.Langfuse` | `LangfuseEnrichmentFilter` — adds `langfuse.*` tags to active Activity. | `LangfuseEnrichmentFilter`, `LangfuseEnrichmentOptions` | You route OTLP to Langfuse and want first-class UI recognition. |
| `Vais.Agents.Observability.Prometheus` | Standalone Prometheus exporter wiring for runtime-tier metrics (distinct from the per-LLM-call `Gateways.Prometheus` middleware). | Extension methods exposed via the runtime composition root | Scraping aggregate runtime metrics into Prometheus. |
| `Vais.Agents.Observability.RunStore` | In-memory + Orleans-grain backed run store. Records per-graph-run state for `vais get-runs`. | `IGraphRunStore`, `OrleansGraphRunStore`, registration helpers | Persisting graph run history for inspection via the CLI / control plane. |
| `Vais.Agents.Observability.AgentRunStore` | Per-agent run history store (single-agent invokes). Companion to `RunStore` for the non-graph path. | Per-agent run store interfaces + Orleans grain | Recording standalone agent invocation history. |
| `Vais.Agents.Observability.AgentLogs` | Structured per-run log sink. Wired into `AiAgentGrain` so each run's events stream into a queryable store. | `IAgentLogSink`, `OrleansAgentLogSink` | Surfacing per-agent run logs via `vais logs`. |
| `Vais.Agents.Observability.GatewayEventStore` | Records LLM gateway middleware events (request, response, errors) per run. | `IGatewayEventStore`, registration helpers | Auditing gateway middleware behaviour or driving dashboards from gateway events. |
| `Vais.Agents.Observability.McpGatewayEventStore` | Records MCP tool-gateway middleware events. | `IMcpGatewayEventStore` | Auditing tool-call gateway behaviour. |
| `Vais.Agents.Observability.McpEventStore` | Records MCP server connection / discovery events (transport-layer, not middleware). | `IMcpEventStore` | Diagnosing MCP server connectivity issues. |

## Protocols — outbound

| Package | Purpose | Key entry points | Install when… |
|---|---|---|---|
| `Vais.Agents.Protocols.Mcp` | `McpToolSource : IToolSource`. Pull tools from an MCP server into the local registry. | `McpToolSource`, `McpToolSourceOptions` | Your agent needs to use tools exposed by an MCP-speaking server. |
| `Vais.Agents.Protocols.A2A` | `A2ARemoteAgentTool : ITool`. Wrap a remote A2A agent as a local tool. | `A2ARemoteAgentTool` | Your agent needs to delegate subtasks to a peer A2A agent. |

## Protocols — inbound

| Package | Purpose | Key entry points | Install when… |
|---|---|---|---|
| `Vais.Agents.Protocols.Mcp.Server` *(v0.7)* | Expose registered agents as MCP tools over stdio or streamable-HTTP. | `AddMcpAgentServerStdio`, `AddMcpAgentServerHttp`, `MapMcpAgentServer`, `McpAgentServerOptions`, `LabelPrefixFilter` | Your host needs to be consumable from Claude Desktop, a ContextForge gateway, or any MCP client. |
| `Vais.Agents.Protocols.A2A.Server` *(v0.8)* | Host agents under `/agents/{id}` with auto-derived `AgentCard`s. Interrupts map to A2A `Task(input-required)`. | `AddA2AAgentServer`, `MapA2AAgentServer`, `AgentCardBuilder`, `A2AJwt` auth scheme, `OrleansTaskStore` | Your host needs to be consumable by A2A peers + directory services. Pair with `Hosting.Orleans` for durable `input-required` tasks. |

## Orchestration (multi-agent)

| Package | Purpose | Key entry points | Install when… |
|---|---|---|---|
| `Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework` *(v0.9)* | `MafGraphOrchestrator<TState>` — translates `AgentGraphManifest` to an MAF `Workflow` and executes via `InProcessExecution`. Alternative to the MAF-free `InProcessGraphOrchestrator` in Core. Implements `IAgentGraph<TState>` + `IResumableAgentGraph<TState>` + `IHitlAgentGraph<TState>`. Supports fan-out / fan-in via `GraphEdge.Concurrent = true`. | `MafGraphOrchestrator<TState>`, `MafGraphBuilder`, `GraphNodeExecutor`, `GraphJoinNodeExecutor` | The host already runs MAF Workflows and you want a single executor surface, or you need declarative fan-out / fan-in. |

## Control plane — core

| Package | Purpose | Key entry points | Install when… |
|---|---|---|---|
| `Vais.Agents.Control.InProcess` *(v0.6)* | In-process `AgentLifecycleManager` wrapping the seven verbs with policy + idempotency + audit middleware. Reference runtime. | `AgentLifecycleManager`, `LoggerAuditLog`, `CompositeSecretResolver` (no service-collection extension — consumers register registry + runtime + lifecycle manager explicitly; see `CompositionRoot.cs` in `Runtime.Host` for the canonical shape) | Running agents with the control-plane verb set, in-process (no HTTP) or as the runtime behind `Control.Http.Server`. |
| `Vais.Agents.Control.Manifests.Json` *(v0.6)* | JSON manifest loader for `kind: Agent` + `kind: AgentGraph` (v0.9) with shared validator. v0.6 envelope shape (`apiVersion` + `kind` + `metadata` + `spec`). | `JsonAgentManifestLoader`, `JsonAgentGraphManifestLoader`, `AgentGraphManifestEnvelope`, `AgentManifestValidationException` | Consuming or emitting manifests over HTTP wire. |
| `Vais.Agents.Control.Manifests.Yaml` *(v0.6)* | YAML manifest loader for the same v0.6 envelope. Normalises YAML to JSON, delegates to the JSON validator. | `YamlAgentManifestLoader`, `YamlAgentGraphManifestLoader` | Authoring manifests in YAML files for local dev, CLI `apply -f`, or Kubernetes CR authors. |

## Control plane — HTTP surface (v0.6 / v0.11 / v0.12)

| Package | Purpose | Key entry points | Install when… |
|---|---|---|---|
| `Vais.Agents.Control.Http.Server` | HTTP surface over `IAgentLifecycleManager` — v0.6 verbs, v0.11 idempotency middleware + OpenAPI emission with `x-vais-type-urns`, v0.12 SSE streaming-invoke route. `InMemoryIdempotencyStore` default. `ProblemDetailsMapping` with every shipped URN. | `AddAgentControlPlane`, `MapAgentControlPlane`, `AddAgentControlPlaneIdempotency`, `AddAgentControlPlaneOpenApi`, `StreamingInvokeOptions`, `[StreamingEndpoint]`, `AgentControlPlaneIdempotencyMiddleware`, `VaisProblemDetailsOperationTransformer` | Running the HTTP control plane in-process. Pulls in `Microsoft.AspNetCore.OpenApi 9.0.11` + `System.Net.ServerSentEvents 10.0.2`. |
| `Vais.Agents.Control.Http.Client` | Typed client over the HTTP surface. v0.12 SSE parsing via `System.Net.ServerSentEvents`. | `AgentControlPlaneClient`, `ClientFactory`, `InvokeStreamAsync` (text-only), `InvokeStreamEventsAsync` (full `AgentEvent`), `AgentControlPlaneException` with `Type` / `Detail` / `Extensions` | Calling an HTTP control plane from another .NET process. |

## Control plane — Kubernetes (v0.13)

| Package | Purpose | Key entry points | Install when… |
|---|---|---|---|
| `Vais.Agents.Control.KubernetesOperator` | `Agent` CRD POCO (`vais.io/v1alpha1`), `IEntityController` implementation with spec-hash-driven reconcile, projected-SA-token `DelegatingHandler`. Pins `KubeOps 10.3.4`. | `AgentEntity`, `AgentSpec`, `AgentStatus`, `AgentPhase`, `AgentEntityController`, `AgentEntityFinalizer`, `ServiceAccountTokenHandler`, `AddAgentKubernetesOperator` | Building a K8s-native declarative deployment. Library only — pair with the in-repo `Vais.Agents.Control.KubernetesOperator.Host` (Dockerfile at `src/…KubernetesOperator.Host/Dockerfile`, not a published NuGet) + Helm chart at `deploy/helm/vais-agents-operator/`. |

## Control plane — Policy engine (v0.14)

| Package | Purpose | Key entry points | Install when… |
|---|---|---|---|
| `Vais.Agents.Control.Policy.Opa` | `OpaPolicyEngine : IAgentPolicyEngine` — pure-HTTP adapter against an external OPA server. Ships the v1 input schema + decision cache (5s TTL, 1024-entry bound, SHA-256 key) + `FailMode = Closed` safe default + "4xx is a bug, 5xx is a policy path" error classification. | `OpaPolicyEngine`, `OpaPolicyEngineOptions`, `OpaFailMode`, `AddOpaPolicyEngine` | Gating every `IAgentLifecycleManager` verb via Rego. Requires an OPA sidecar / standalone server reachable from the control-plane pod. |

## Control plane — MCP gateway / server registry

| Package | Purpose | Key entry points | Install when… |
|---|---|---|---|
| `Vais.Agents.Control.Mcp` | `PhysicalMcpConnectionService` (manages stdio + streamable-HTTP MCP connections per registered server), `IMcpServerRegistry` lifecycle, virtual-MCP binding (`VirtualMcpToolSource`). | `PhysicalMcpConnectionService`, `IMcpServerRegistry`, `IMcpServerLifecycleManager`, registration helpers | Hosting an MCP server registry under the control plane — required when manifests reference `mcp:<server>` tool sources or when the runtime exposes a virtual MCP gateway. |

## Identity (v0.29)

| Package | Purpose | Key entry points | Install when… |
|---|---|---|---|
| `Vais.Agents.Identity.Oidc` | `OidcAgentIdentityProvider : IAgentIdentityProvider`. Inbound JWT validation via OIDC discovery + JWKS; outbound `client_credentials` token acquisition with per-`(agentId, credentialRef)` cache. Works with Keycloak, Auth0, Microsoft Entra, any OIDC-compliant IdP. v0.30 adds `ServiceAccountPrincipalMapper` for Kubernetes service-account JWTs. | `OidcAgentIdentityProvider`, `ServiceAccountPrincipalMapper`, `AddOidcAgentIdentity` | Production identity — JWT bearer auth on the HTTP control plane + downstream `client_credentials` token acquisition. |

## Eval harness (E1..E4)

| Package | Purpose | Key entry points | Install when… |
|---|---|---|---|
| `Vais.Agents.Eval` | `EvalSuite` manifest + `EvalRunGrain` lifecycle + four built-in assertions (response-equals, response-contains, response-regex, JSON-path) + SSE-streamed progress + result diff + JUnit export. Surfaced via `vais eval run|results|list|cancel|diff`. | `EvalSuiteManifest`, `EvalRunGrain`, `EvalAssertion`, registration helpers | Running regression evals against agents. See [evaluate-an-agent tutorial](../tutorials/evaluate-an-agent.md). |

## Runtime container (v0.16)

| Artefact | Purpose | Entry points | Ship when… |
|---|---|---|---|
| `vais-agents-runtime` container image | Opinionated, Orleans-only deployable binding of the library stack. Composition root wires durability sidecars in the correct order, HTTP control plane, `/healthz` + `/readyz` probes, optional OPA sidecar, optional OTel / Langfuse. | Built from `src/Vais.Agents.Runtime.Host/Dockerfile`; not a NuGet. Environment-variable driven (see [runtime-configuration reference](./runtime-configuration.md)). docker-compose recipes: [`deploy/compose/`](../../deploy/compose/README.md). Helm chart: [`deploy/helm/vais-agents-runtime/`](../../deploy/helm/vais-agents-runtime/README.md). | Partner evaluation, Kubernetes deployment, anyone who wants the runtime as a product. Custom hosts / embedded agents / unusual runtimes keep composing the library directly. |

## Manifest-driven instantiation (v0.17)

| Package | Purpose | Key entry points | Install when… |
|---|---|---|---|
| `Vais.Agents.Runtime.Instantiation` *(v0.17)* | Translates a stored `AgentManifest` into `StatefulAgentOptions` ready to seed a `StatefulAiAgent`. Ships three built-in model-provider factories (OpenAI / Anthropic / Azure-OpenAI via MEAI `IChatClient`) + the `IGuardrailFactory` chain + `IStaticToolRegistry` + `IPromptTemplateRegistry` + `IPromptFileLoader` seams. Opinionated glue; sits above `Core` + `Control.Abstractions` and below `Runtime.Host`. v0.18 adds a plugin-branch before the declarative path + two new URNs (`plugin-factory-throw`, `handler-and-declarative-fields-both-set`). | `IAgentManifestTranslator`, `IModelProviderFactory`, `IGuardrailFactory`, `IStaticToolRegistry`, `IPromptTemplateRegistry`, `IPromptFileLoader`, `ICompletionProviderPool`, `ManifestInstantiationException`, `AddAgentManifestInstantiator`, `AddBuiltinModelProviders`, `AddBuiltinGuardrails`, `AddStaticToolRegistry`, `AddPromptTemplateRegistry`, `AddFileSystemPromptFileLoader`, `OpenAIModelProviderFactory`, `AnthropicModelProviderFactory`, `AzureOpenAIModelProviderFactory` | Hosting a runtime that accepts YAML-authored agents without consumer-written C#. The v0.16 container image wires this automatically; custom hosts opt in via the DI extensions. Four `Vais.Agents.Core.Guardrails.*` built-ins (LengthCap / RegexAllowlist × 2 / RegexDenylist × 2 / LlmAsJudge) live in `Core`; the factories here dispatch to them. |

## Plugin model

| Package | Purpose | Key entry points | Install when… |
|---|---|---|---|
| `Vais.Agents.Runtime.Plugins` *(v0.18 — assembly model)* | Plugin loader. Scans `/var/lib/vais/plugins/*/` at silo startup, loads each subfolder into its own `PluginAssemblyLoadContext` with a shared-types carve-out across the DI seam, and registers exported `IAgentHandlerFactory` / `IAiAgent` types in an `IPluginHandlerRegistry` singleton. The manifest translator queries the registry before the declarative `Model` check so plugin-authored agents take precedence for matching `Handler.TypeName` values. v0.22 adds hot reload (`DefaultPluginReloader`, `IPluginReloadHook`). | `AssemblyPluginLoader`, `IPluginHandlerRegistry`, `PluginAssemblyLoadContext`, `PluginLoaderOptions`, `PluginLoadException`, `PluginUrns`, `VaisRuntimeAbi`, `DefaultHandlerFactory<T>`, `DefaultPluginReloader`, `AddAgentPlugins` | Hosting a runtime that accepts code-authored agents packaged as DLLs. Plugin-authoring contract (`IAgentHandlerFactory`, `VaisPluginAttribute`) + the `Agent` slot on `StatefulAgentOptions` live in `Abstractions` + `Core`. |
| `Vais.Agents.Runtime.Plugins.Python` *(v0.23 / v0.24)* | Python subprocess plugin loader. Discovers `plugin.yaml` under the plugins directory, spawns the Python interpreter, performs the JSON-RPC handshake over stdio, and exposes either tools (MCP server scope) or a full agent shim (`PythonAgentShim : IAiAgent + IStreamingAiAgent`). Hot reload via `PythonPluginWatcherService` (v0.25). | `PythonPluginHost`, `PythonAgentShim`, `PythonPluginWatcherService`, `PythonPluginUrns`, `AddAgenticPythonPlugins` | Hosting Python-authored tools or agents alongside the .NET runtime. See [polyglot-plugins concept](../concepts/polyglot-plugins.md). |
| `Vais.Agents.Runtime.Plugins.Container` *(IP-1..IP-7 + CP-1..CP-9)* | Container plugin supervisor + HTTP gateway protocol + OTLP receiver. Manages OCI-image plugins as Docker containers or Kubernetes Deployments; HMAC-token-authed inbound LLM/MCP/OTLP gateway on port 5001; Phase 1 sandbox hardening on every container, Phase 2 internal-network egress isolation opt-in. | `ContainerPluginManifest`, `ContainerPluginDescriptor`, `IContainerSupervisor`, `DockerContainerSupervisor`, `KubernetesContainerSupervisor`, `IContainerPluginHost`, `IContainerPluginReloader`, `OtlpSpanForwarder`, `HmacCallTokenService`, `ContainerPluginUrns` | Hosting OCI-image plugins. See [container-plugins concept](../concepts/container-plugins.md). |
| `Vais.Plugin.Sdk` *(IP-1)* | SDK for building container plugins against the IP-1 HTTP protocol. ASP.NET Core minimal-API extensions for `/invoke`, gateway-client helpers, health endpoints. | `PluginEndpointRouteBuilderExtensions`, gateway-client types | Authoring a .NET container plugin. Python container plugins use the `vais-plugin` PyPI package (separately distributed). |

## Extensions (v0.30)

| Package | Purpose | Key entry points | Install when… |
|---|---|---|---|
| `Vais.Agents.Runtime.Extensions` | Extension loader for runtime seams. Loads seam middleware (`AgentInputMiddleware`, `AgentOutputMiddleware`, …) into **collectible** `AssemblyLoadContext`s and composes per-agent chains; bound to agents declaratively via the control plane and reloadable without a runtime restart. Phase A: C# host. | Extension loader + per-agent chain composition types, registration helpers | Hosting a runtime that accepts code-authored seam extensions bound to agents declaratively. See [extensions docs](../extensions/index.md). |

## CLI (v0.15+)

| Package | Purpose | Key entry points | Install when… |
|---|---|---|---|
| `Vais.Agents.Cli` | `vais` dotnet-tool wrapping the HTTP control plane. ~30 top-level commands + 5 branches (`eval`, `ext`, `agent`, `diagnose`, `config`) for a total of ~47 commands covering agents, graphs, gateways, plugins, extensions, eval, diagnostics, and config. Kubeconfig-style `~/.vais/config.yaml`. POSIX exit codes. | `vais apply` / `invoke` / `logs` / `signal` / `get` / `delete` / `cancel` / `init` / `version`; graph commands (`get-graphs`, `delete-graph`, `graph-validate`, `invoke-graph`, `graph-logs`, `get-runs`); plugin commands (`plugin-status`, `plugin-push`, `plugin-build`, `plugin-deploy`, `plugin-init`, `plugin-watch`, `plugin-import-existing`); gateway / MCP server (`get-llm-gateways`, `get-mcp-gateways`, `get-mcp-servers`, `*-validate`); eval branch; ext branch; agent branch; diagnose branch; config branch | Interactive exploration + CI manifest apply + log tailing. Install with `dotnet tool install -g Vais.Agents.Cli`. Cannot be added as a library reference (NU1212) — use `Vais.Agents.Control.Http.Client` for in-process .NET callers. |

`Vais.Agents.Cli` ships with `<PackAsTool>true</PackAsTool>` + `<ToolCommandName>vais</ToolCommandName>`. Targets `net9.0`. The tool install resolves the binary under `~/.dotnet/tools/` (`%USERPROFILE%\.dotnet\tools\` on Windows). Full command catalogue: [cli-subcommands reference](cli-subcommands.md).

## Typical scenario bundles

- **Provider fallback** — `Gateways.Fallback` + any provider adapter. Construct an `InMemoryFallbackProviderPool`, wrap it in `LlmFallbackMiddleware`, add to `GatewayMiddleware`.
- **Rate-limited multi-tenant API** — `Gateways.Governance`. One `LlmRateLimitMiddleware` per agent; key resolves from `AgentContext.UserId` / `TenantId`.
- **Structured JSON output** — `Gateways.StructuredOutput`. `new LlmJsonOutputMiddleware<MyDto>()` added first in `GatewayMiddleware`; the mock middleware (or real provider) sits behind it.
- **Agent unit tests without LLM** — `Gateways.Testing`. Replace provider calls with `LlmMockMiddleware(response1, response2, …)` in `GatewayMiddleware`; the real provider is never called.
- **Single-process agent on SK** — `Abstractions` + `Core` + `Ai.SemanticKernel`.
- **Single-process agent on MAF** — `Abstractions` + `Core` + `Ai.MicrosoftAgentFramework`.
- **Clustered agent on Orleans + Redis** — above + `Hosting.Orleans` + `Persistence.Redis`.
- **Clustered agent on Orleans + Postgres** — above + `Hosting.Orleans` + `Persistence.Postgres`.
- **Observability stack** — any of the above + `Observability.OpenTelemetry` (+ optionally `Observability.Langfuse`).
- **RAG-augmented** — any of the above + `Persistence.VectorData`.
- **MCP + A2A outbound interop** — any of the above + `Protocols.Mcp` + `Protocols.A2A`.
- **Agents as MCP / A2A servers** *(v0.7 + v0.8)* — any of the above + `Protocols.Mcp.Server` + `Protocols.A2A.Server`. Pair with `Hosting.Orleans` + `AddOrleansA2ATaskStore` for durable A2A `input-required` tasks.
- **Reliable tool calls** — any of the above + `Gateways.McpReliability`. Add `ToolCircuitBreakerMiddleware` + `ToolTimeoutGuard` + `ToolRetryMiddleware` to `ToolGatewayMiddleware`.
- **Tool result caching** — any of the above + `Gateways.McpCache`. Exclude side-effectful tools via the `excludedTools` list.
- **Multi-tenant tool governance** — any of the above + `Gateways.McpGovernance`. One `ToolRateLimitMiddleware` per tenant key; `ToolWorkspacePolicyMiddleware` enforces per-workspace allow/deny rules.
- **Tool security hardening** — any of the above + `Gateways.McpSecurity`. `ToolArgumentValidationMiddleware` before `ToolOutputLengthGuard`.
- **Graph orchestration** *(v0.9)* — any of the above + `Core` (ships `InProcessGraphOrchestrator`) + `Control.Manifests.Yaml` for YAML-authored graphs + optional `Orchestration.Graph.MicrosoftAgentFramework` for the MAF-Workflows adapter. Pair with `Hosting.Orleans` + `AddOrleansGraphCheckpointer` for durable interrupt/resume. Add `Core.PowerFx` + `AddPowerFxExpressionEvaluator()` to enable inline `when: "=..."` PowerFx edge predicates.
- **HTTP control plane with idempotency + OpenAPI** *(v0.6 + v0.11)* — `Control.InProcess` + `Control.Http.Server` + `AddAgentControlPlaneIdempotency` + `AddAgentControlPlaneOpenApi`. Pair with `Hosting.Orleans` + `AddOrleansIdempotencyStore` for durable deduplication across silo restart.
- **HTTP streaming invoke** *(v0.12)* — `Control.Http.Server` (exposes `/v1/agents/{id}/invoke/stream` SSE route) + `Control.Http.Client` (`InvokeStreamEventsAsync` / `InvokeStreamAsync`). Agent must implement `IStreamingAiAgent` (`StatefulAiAgent` does out of the box).
- **Kubernetes-native deployment** *(v0.13)* — `Control.KubernetesOperator` + in-repo `KubernetesOperator.Host` container + `deploy/helm/vais-agents-operator/` chart + `deploy/crds/vais.io_agents.yaml`. Pair with your `Control.Http.Server`-hosted runtime reachable from cluster pods.
- **OPA-gated control plane** *(v0.14)* — `Control.Policy.Opa` + an OPA sidecar / standalone server + a Rego bundle. Pair with `Control.Http.Server` so policy decisions surface as `403 urn:vais-agents:policy-denied` on the HTTP wire.
- **CLI over the HTTP control plane** *(v0.15)* — `dotnet tool install -g Vais.Agents.Cli` → `vais` on PATH. Pairs with any `Control.Http.Server`-hosted runtime for interactive ops + CI manifest-apply.
- **Runtime as a container** *(v0.16)* — `docker build -f src/Vais.Agents.Runtime.Host/Dockerfile .` → `vais-agents-runtime` image. docker-compose recipes in `deploy/compose/`; Helm chart in `deploy/helm/vais-agents-runtime/`. v0.17 resolves invoke for declarative YAML agents; v0.18 adds a plugin loader for code-authored agents shipped as DLLs under `/var/lib/vais/plugins`.
- **Plugin-authored agent shipped as a DLL** *(v0.18)* — runtime container + `Vais.Agents.Abstractions` + `Vais.Agents.Core` in a separate `classlib` publish + `[assembly: VaisPlugin(...)]`. Ship as an overlay image (`FROM vais-agents-runtime` + `COPY ./publish /var/lib/vais/plugins/...`). Walk-through: [Package an agent as a plugin](../guides/package-an-agent-as-a-plugin.md).
- **Graph as a first-class deployable** *(v0.19)* — apply `kind: AgentGraph` manifests via `vais apply`, invoke via `vais invoke-graph`, stream via `vais invoke-graph --stream`. Graph manifests persist in `OrleansAgentGraphRegistry` + project into the K8s CRD. No extra NuGet — wired in the runtime host's `CompositionRoot`.
- **Cross-runtime graph refs** *(v0.20)* — set `ref.runtimeUrl` on any `kind: Agent` node to dispatch it to a remote runtime. `IAgentRemoteInvoker` (`Control.Http.Client`) handles the HTTP forwarding + bearer-token propagation. Walk-through: [compose-a-graph-across-runtimes guide](../guides/compose-a-graph-across-runtimes.md).
- **OpenAI-compatible gateway** — `Gateways.OpenAiCompat` + `Core` (ships `InMemoryModelRouter`, `PassThroughIdentityResolver`, `LlmGatewayPipeline`) + any provider adapter. Wire `AddOpenAiCompatGateway()` + `MapOpenAiCompat()` on `WebApplication`. Add other `Gateways.*` middleware for rate limiting, caching, fallback, etc. Guide: [expose-openai-compatible-gateway](../guides/expose-openai-compatible-gateway.md).
- **Prometheus LLM metrics** — any gateway setup above + `Gateways.Prometheus`. `AddLlmPrometheusMiddleware()` registers `LlmPrometheusMiddleware` as the outermost middleware; `llm_requests_total`, `llm_request_duration_seconds`, and `llm_tokens_total` are automatically scraped.
- **Everything** — all 54 NuGet packages + the runtime container; see `artifacts/smoketest/` for what the full library stack looks like.

## Version pins (in `Directory.Packages.props`)

See [installation](../library-mode/installation.md) for the full pin list — SK 1.74, MAF 1.1.0, MEAI 10.5.0, OpenAI 2.10.0, Orleans 10.1.0, MCP 1.2.0, A2A 1.0.0-preview2, OTel 1.15.2, AspNetCore.OpenApi 9.0.11, ServerSentEvents 10.0.2, KubeOps 10.3.4, Spectre.Console.Cli 0.55.0, prometheus-net 8.2.1.

## See also

- [Architecture concept](../concepts/architecture.md) — dependency layering diagram.
- [Installation](../library-mode/installation.md)
- [Install the CLI](../devops/install-the-cli.md)
