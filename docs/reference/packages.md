# Reference: packages

All **27 packages** under the `Vais.Agents.*` prefix — 26 libraries plus the `Vais.Agents.Cli` dotnet tool. Target framework: `net9.0`. Version: `0.18.0-preview` (not yet published to nuget.org). This page is the canonical list — every shipped package with one-line purpose, key entry points, and install guidance.

Plus one **in-repo composition project** — `Vais.Agents.Runtime.Host` — that builds the `vais-agents-runtime` container image (v0.16 Pillar A). Not a NuGet; ships as the Dockerfile + docker-compose recipes + Helm chart under [`deploy/`](../../deploy/). See the [Runtime container](#runtime-container-v016) row at the bottom of this page.

## Contracts

| Package | Purpose | Key entry points | Install when… |
|---|---|---|---|
| `Vais.Agents.Abstractions` | Neutral agent + provider + memory + context + tool + event contracts. No SK / MAF / Orleans / AspNetCore deps. | `ICompletionProvider`, `IAiAgent`, `IStreamingAiAgent`, `IAgentGraph<TState>`, `AgentEvent` (9 subclasses), `AgentGraphEvent` (9 subclasses), `AgentManifest`, `RunBudget`, `ITool` | Authoring a custom adapter or host. Otherwise pulled transitively. |
| `Vais.Agents.Control.Abstractions` | Control-plane verb contracts + idempotency / policy / audit / secret interfaces. | `IAgentLifecycleManager`, `IAgentPolicyEngine`, `IIdempotencyStore`, `IAgentAuditLog`, `IAgentSecretResolver`, `PolicyDecision`, `PolicyOperation` | Authoring a custom control plane or policy engine. Otherwise pulled transitively. |

## Core

| Package | Purpose | Key entry points | Install when… |
|---|---|---|---|
| `Vais.Agents.Core` | Default `StatefulAiAgent` + execution loop + in-process defaults + diagnostics constants + `InProcessGraphOrchestrator` (zero-MAF-dep) + `InMemoryCheckpointer`. | `StatefulAiAgent`, `StatefulAgentOptions`, `DefaultToolCallDispatcher`, `InProcessGraphOrchestrator<TState>`, `AgenticDiagnostics`, `AgenticTags` | Any scenario that builds or runs an agent. |

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
| `Vais.Agents.Observability.OpenTelemetry` | `OpenTelemetryUsageSink` + `AddAgenticInstrumentation` extensions for Tracer / Meter providers. | `OpenTelemetryUsageSink`, `AddAgenticInstrumentation` | Exporting traces + metrics to any OTel collector (Jaeger, Tempo, Datadog, Grafana). |
| `Vais.Agents.Observability.Langfuse` | `LangfuseEnrichmentFilter` — adds `langfuse.*` tags to active Activity. | `LangfuseEnrichmentFilter`, `LangfuseEnrichmentOptions` | You route OTLP to Langfuse and want first-class UI recognition. |

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
| `Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework` *(v0.9)* | `MafGraphOrchestrator<TState>` — translates `AgentGraphManifest` to an MAF `Workflow` and executes via `InProcessExecution`. Alternative to the MAF-free `InProcessGraphOrchestrator` in Core. | `MafGraphOrchestrator<TState>`, `MafGraphBuilder` | The host already runs MAF Workflows and you want a single executor surface. Does **not** implement `IResumableAgentGraph<TState>` — use `InProcessGraphOrchestrator` + `OrleansCheckpointer` for durable resume today. |

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

## Runtime container (v0.16)

| Artefact | Purpose | Entry points | Ship when… |
|---|---|---|---|
| `vais-agents-runtime` container image | Opinionated, Orleans-only deployable binding of the library stack. Composition root wires durability sidecars in the correct order, HTTP control plane, `/healthz` + `/readyz` probes, optional OPA sidecar, optional OTel / Langfuse. | Built from `src/Vais.Agents.Runtime.Host/Dockerfile`; not a NuGet. Environment-variable driven (see [runtime-configuration reference](./runtime-configuration.md)). docker-compose recipes: [`deploy/compose/`](../../deploy/compose/README.md). Helm chart: [`deploy/helm/vais-agents-runtime/`](../../deploy/helm/vais-agents-runtime/README.md). | Partner evaluation, Kubernetes deployment, anyone who wants the runtime as a product. Custom hosts / embedded agents / unusual runtimes keep composing the library directly. |

## Manifest-driven instantiation (v0.17)

| Package | Purpose | Key entry points | Install when… |
|---|---|---|---|
| `Vais.Agents.Runtime.Instantiation` *(v0.17)* | Translates a stored `AgentManifest` into `StatefulAgentOptions` ready to seed a `StatefulAiAgent`. Ships three built-in model-provider factories (OpenAI / Anthropic / Azure-OpenAI via MEAI `IChatClient`) + the `IGuardrailFactory` chain + `IStaticToolRegistry` + `IPromptTemplateRegistry` + `IPromptFileLoader` seams. Opinionated glue; sits above `Core` + `Control.Abstractions` and below `Runtime.Host`. v0.18 adds a plugin-branch before the declarative path + two new URNs (`plugin-factory-throw`, `handler-and-declarative-fields-both-set`). | `IAgentManifestTranslator`, `IModelProviderFactory`, `IGuardrailFactory`, `IStaticToolRegistry`, `IPromptTemplateRegistry`, `IPromptFileLoader`, `ICompletionProviderPool`, `ManifestInstantiationException`, `AddAgentManifestInstantiator`, `AddBuiltinModelProviders`, `AddBuiltinGuardrails`, `AddStaticToolRegistry`, `AddPromptTemplateRegistry`, `AddFileSystemPromptFileLoader`, `OpenAIModelProviderFactory`, `AnthropicModelProviderFactory`, `AzureOpenAIModelProviderFactory` | Hosting a runtime that accepts YAML-authored agents without consumer-written C#. The v0.16 container image wires this automatically; custom hosts opt in via the DI extensions. Four `Vais.Agents.Core.Guardrails.*` built-ins (LengthCap / RegexAllowlist × 2 / RegexDenylist × 2 / LlmAsJudge) live in `Core`; the factories here dispatch to them. |

## Plugin model (v0.18)

| Package | Purpose | Key entry points | Install when… |
|---|---|---|---|
| `Vais.Agents.Runtime.Plugins` *(v0.18)* | v0.18 Pillar C plugin loader. Scans `/var/lib/vais/plugins/*/` at silo startup, loads each subfolder into its own `PluginAssemblyLoadContext` with a shared-types carve-out across the DI seam, and registers exported `IAgentHandlerFactory` / `IAiAgent` types in an `IPluginHandlerRegistry` singleton. The manifest translator queries the registry before the declarative `Model` check so plugin-authored agents take precedence for matching `Handler.TypeName` values. | `AssemblyPluginLoader`, `IPluginHandlerRegistry`, `PluginHandlerRegistry`, `PluginAssemblyLoadContext`, `PluginLoaderOptions`, `PluginLoadException`, `PluginUrns`, `VaisRuntimeAbi`, `DefaultHandlerFactory<T>`, `AddAgentPlugins` | Hosting a runtime that accepts code-authored agents packaged as DLLs. The v0.16 container image wires this automatically when `VAIS_PLUGINS_DIRECTORY` resolves to a readable directory; custom hosts opt in via `services.AddAgentPlugins(path)`. Plugin-authoring contract (`IAgentHandlerFactory`, `VaisPluginAttribute`) + the `Agent` slot on `StatefulAgentOptions` live in `Abstractions` + `Core`. |

## CLI (v0.15)

| Package | Purpose | Key entry points | Install when… |
|---|---|---|---|
| `Vais.Agents.Cli` | `vais` dotnet-tool wrapping the v0.6 HTTP control plane. Thirteen subcommands (9 verbs + `config` branch with 4 sub-verbs). Kubeconfig-style `~/.vais/config.yaml`. POSIX exit codes. | `vais apply`, `vais invoke`, `vais logs`, `vais signal`, `vais get`, `vais delete`, `vais cancel`, `vais init`, `vais version`, `vais config {get-contexts, current-context, use-context, set-context}` | Interactive exploration + CI manifest apply + log tailing. Install with `dotnet tool install -g Vais.Agents.Cli`. Cannot be added as a library reference (NU1212) — use `Vais.Agents.Control.Http.Client` for in-process .NET callers. |

`Vais.Agents.Cli` ships with `<PackAsTool>true</PackAsTool>` + `<ToolCommandName>vais</ToolCommandName>`. Targets `net9.0`. The tool install resolves the binary under `~/.dotnet/tools/` (`%USERPROFILE%\.dotnet\tools\` on Windows).

## Typical scenario bundles

- **Single-process agent on SK** — `Abstractions` + `Core` + `Ai.SemanticKernel`.
- **Single-process agent on MAF** — `Abstractions` + `Core` + `Ai.MicrosoftAgentFramework`.
- **Clustered agent on Orleans + Redis** — above + `Hosting.Orleans` + `Persistence.Redis`.
- **Clustered agent on Orleans + Postgres** — above + `Hosting.Orleans` + `Persistence.Postgres`.
- **Observability stack** — any of the above + `Observability.OpenTelemetry` (+ optionally `Observability.Langfuse`).
- **RAG-augmented** — any of the above + `Persistence.VectorData`.
- **MCP + A2A outbound interop** — any of the above + `Protocols.Mcp` + `Protocols.A2A`.
- **Agents as MCP / A2A servers** *(v0.7 + v0.8)* — any of the above + `Protocols.Mcp.Server` + `Protocols.A2A.Server`. Pair with `Hosting.Orleans` + `AddOrleansA2ATaskStore` for durable A2A `input-required` tasks.
- **Graph orchestration** *(v0.9)* — any of the above + `Core` (ships `InProcessGraphOrchestrator`) + `Control.Manifests.Yaml` for YAML-authored graphs + optional `Orchestration.Graph.MicrosoftAgentFramework` for the MAF-Workflows adapter. Pair with `Hosting.Orleans` + `AddOrleansGraphCheckpointer` for durable interrupt/resume.
- **HTTP control plane with idempotency + OpenAPI** *(v0.6 + v0.11)* — `Control.InProcess` + `Control.Http.Server` + `AddAgentControlPlaneIdempotency` + `AddAgentControlPlaneOpenApi`. Pair with `Hosting.Orleans` + `AddOrleansIdempotencyStore` for durable deduplication across silo restart.
- **HTTP streaming invoke** *(v0.12)* — `Control.Http.Server` (exposes `/v1/agents/{id}/invoke/stream` SSE route) + `Control.Http.Client` (`InvokeStreamEventsAsync` / `InvokeStreamAsync`). Agent must implement `IStreamingAiAgent` (`StatefulAiAgent` does out of the box).
- **Kubernetes-native deployment** *(v0.13)* — `Control.KubernetesOperator` + in-repo `KubernetesOperator.Host` container + `deploy/helm/vais-agents-operator/` chart + `deploy/crds/vais.io_agents.yaml`. Pair with your `Control.Http.Server`-hosted runtime reachable from cluster pods.
- **OPA-gated control plane** *(v0.14)* — `Control.Policy.Opa` + an OPA sidecar / standalone server + a Rego bundle. Pair with `Control.Http.Server` so policy decisions surface as `403 urn:vais-agents:policy-denied` on the HTTP wire.
- **CLI over the HTTP control plane** *(v0.15)* — `dotnet tool install -g Vais.Agents.Cli` → `vais` on PATH. Pairs with any `Control.Http.Server`-hosted runtime for interactive ops + CI manifest-apply.
- **Runtime as a container** *(v0.16 Pillar A)* — `docker build -f src/Vais.Agents.Runtime.Host/Dockerfile .` → `vais-agents-runtime:0.18.0-preview`. docker-compose recipes in `deploy/compose/`; Helm chart in `deploy/helm/vais-agents-runtime/`. v0.17 Pillar B resolves invoke for declarative YAML agents; v0.18 Pillar C adds a plugin loader for code-authored agents shipped as DLLs under `/var/lib/vais/plugins`.
- **Plugin-authored agent shipped as a DLL** *(v0.18 Pillar C)* — runtime container + `Vais.Agents.Abstractions` + `Vais.Agents.Core` in a separate `classlib` publish + `[assembly: VaisPlugin(...)]`. Ship as an overlay image (`FROM vais-agents-runtime` + `COPY ./publish /var/lib/vais/plugins/...`). Walk-through: [package-an-agent-as-a-plugin guide](../guides/package-an-agent-as-a-plugin.md).
- **Everything** — 26 packages + the runtime container; see `artifacts/smoketest/` for what the full library stack looks like.

## Version pins (in `Directory.Packages.props`)

See [installation](../getting-started/installation.md) for the full pin list — SK 1.74, MAF 1.1.0, MEAI 10.5.0, OpenAI 2.10.0, Orleans 10.1.0, MCP 1.2.0, A2A 1.0.0-preview2, OTel 1.15.2, AspNetCore.OpenApi 9.0.11, ServerSentEvents 10.0.2, KubeOps 10.3.4, Spectre.Console.Cli 0.55.0.

## See also

- [Architecture concept](../concepts/architecture.md) — dependency layering diagram.
- [Installation](../getting-started/installation.md)
- [Install the CLI](../getting-started/install-the-cli.md)
