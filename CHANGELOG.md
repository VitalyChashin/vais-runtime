# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Version scheme: `0.X.0-preview` where X is the pillar number. Breaking changes are expected until the first tagged alpha.

---

## [Unreleased]

### Added
- **`vais invoke-graph --output state`** — new output mode that serialises the final state bag as indented JSON to stdout. Useful for piping state into subsequent tools or scripts. Existing `text` and `json` modes are unchanged.
- **`PythonPluginsReadyCheck`** — new `IHealthCheck` registered on `/readyz` (tag `"ready"`) when `VAIS_PYTHON_PLUGINS_DIRECTORY` is set. Reports `Healthy` when all Python plugins reach `Ready`; `Degraded` while any plugin is still `Loading`/`Restarting`; `Unhealthy` when any plugin is `Unavailable`. Prevents Kubernetes from routing traffic to a pod whose Python plugins have not completed startup.
- **`vais plugin-status`** — new CLI command listing all loaded plugins (assembly + Python subprocess) in a table showing name, kind, state, API version, handler / tool names, PID, and last stderr snippet for failed Python plugins. Supports `-o json` and `-o yaml` output modes.
- **`GET /v1/plugins` extended for Python plugins.** Response now includes Python subprocess plugins alongside .NET assembly plugins. Each entry carries new fields: `kind` (`Assembly`/`Python`), `state` (`Loading`/`Ready`/`Restarting`/`Unavailable`), `processId`, `toolNames` (Python MCP tools), and `lastErrorSnippet` (last stderr lines when a Python plugin fails).
- **`IAgentControlPlaneClient.ListPluginsAsync`** — new typed HTTP client method for `GET /v1/plugins`. Default interface implementation returns an empty response so mock implementations need no changes.
- **`LoadedPythonPlugin.LastErrorSnippet`** — new `init` property exposing the last 3 stderr lines from the most recent subprocess spawn. Surfaced in `/v1/plugins` and `vais plugin-status` so operators can diagnose Python plugin import failures without separately streaming subprocess logs.
- **`POST /v1/graphs/validate`** — new stateless dry-run endpoint (v0.38). Accepts an `AgentGraph` manifest in the request body and returns `GraphValidationResult` with `valid` (bool) and `errors` (string list) — always `200 OK`. Performs structural checks via `AgentGraphManifestValidator` then runtime-context checks: Code-kind nodes are cross-referenced against `IPluginHandlerRegistry`; Agent-kind nodes are cross-referenced against `IAgentRegistry`. Surfaces at CI time the class of errors that previously only appeared at invoke time.
- **`vais graph-validate -f manifest.yaml`** — new CLI command driving `POST /v1/graphs/validate`. Prints `valid <id>` on success; lists errors on failure. Exit code 0 = valid, 1 = errors. Supports `-o json` / `-o yaml` for structured output.
- **`IAgentControlPlaneClient.ValidateGraphAsync`** — new typed HTTP client method for `POST /v1/graphs/validate`. Default interface implementation returns a passing result so mock implementations need no changes.
- Docs: `docs/guides/run-python-plugins-on-windows.md` — new guide documenting Git Bash MSYS path-conversion footguns (`interpreter` path, volume mounts, CLI arguments) and the fixes (`//` prefix, `MSYS_NO_PATHCONV=1`, Windows-native paths, PowerShell).
- Docs: `docs/guides/wrap-third-party-tools.md` — new guide covering DI introspection of `INamedToolSourceProvider` registrations and the override/wrap pattern for customising third-party tool sources.
- `AssemblyPluginLoader.ResolvePrimaryAssembly` now resolves the primary DLL when the folder contains multiple assemblies (e.g. `CopyLocalLockFileAssemblies=true`) by trying a suffix match (`<Something>.<PluginName>.dll`) after the exact-name match fails. Previously returned null for all multi-DLL folders.
- `AssemblyPluginLoader.TryRegisterHandler` now falls back to a simple-name scan when `assembly.GetType(fullName)` returns null, so `[VaisPlugin(..., "WeatherAgent")]` resolves to `MyApp.WeatherAgent.WeatherAgent` and logs the full CLR name as a hint. Previously the plugin was silently skipped.
- `PythonSubprocessSupervisor` now buffers the last 20 stderr lines per subprocess spawn cycle and includes them in both the MCP handshake-timeout warning and any other handshake failure (`Exception`), so the Python traceback is visible without separately streaming subprocess logs. When stderr contains `ImportError` or `AttributeError`, a targeted `LogError` is emitted recommending moving module-level `AgentDefinition` construction inside a function.
- `PluginManifestConsistencyCheck` — new `IHostedService` registered in `CompositionRoot` that walks every registered `AgentManifest` at startup and logs `LogError` for each manifest whose `Handler.TypeName` does not resolve to a loaded plugin handler. Surfaces mis-deployed plugins at boot time rather than at first invocation.
- `services.AddHttpClient()` registered in `CompositionRoot.ConfigureServices` so `IHttpClientFactory` is available to plugin handler constructors without any extra registration. Previously documented as available but never wired.
- `FallbackUvSync = true` in `PythonPluginLoaderOptions` when `VAIS_MODE=localhost` — automatically runs `uv sync --frozen` inside the plugin directory when `.venv/` is absent, removing the manual setup step for new contributors.
- Startup warning emitted to stderr when `VAIS_LANGFUSE_PROJECT` is set but neither `VAIS_OTEL_ENDPOINT` nor `VAIS_OTEL_CONSOLE` is configured, so the "Langfuse traces are silently dropped" footgun is surfaced at boot time.
- `GraphEventRenderer` default case now applies `EscapeMarkup` to the event type name, preventing a potential Spectre.Console crash if an unknown `AgentGraphEvent` subtype with brackets in its name is received.
- `OpenAIModelProviderFactory` now honours `ModelSpec.BaseUrlRef`: when set, the resolved value is used as the API endpoint, enabling any OpenAI-compatible service (local models, proxies, SGR Agent, etc.) to be consumed without additional code. Behaviour is unchanged when `BaseUrlRef` is absent.
- `PythonAgentShim` — `IAiAgent` backed by a Python subprocess via the new `vais/agent.*` JSON-RPC MCP extension (v0.24). Supports opaque state round-trips so Python agents maintain their own internal state across turns without the .NET side parsing it.
- `IOpaqueStateCarrier` interface wired through `AiAgentGrain` — the grain persists the opaque blob alongside history so Python agent state survives silo restarts.
- `AgentInvokeRequest` / `AgentInvokeResponse` JSON-RPC protocol types and `IPythonAgentChannel` abstraction for the Python subprocess channel.

### Changed
- **Breaking:** `IAgentManifestTranslator.TranslateForGrain` signature changed from `StatefulAgentOptions TranslateForGrain(IServiceProvider, string)` (sync) to `ValueTask<StatefulAgentOptions> TranslateForGrain(IServiceProvider, string, CancellationToken)` (async). Update call sites from `translator.TranslateForGrain(sp, id)` to `await translator.TranslateForGrain(sp, id, ct)`.
- **Breaking:** `ConfigureAgentGrains` now accepts `Func<IServiceProvider, string, CancellationToken, ValueTask<StatefulAgentOptions>>?` instead of `Func<IServiceProvider, string, StatefulAgentOptions>?`. The DI-registered type changes from `Func<string, StatefulAgentOptions>` to `Func<string, CancellationToken, ValueTask<StatefulAgentOptions>>`. Update lambdas from `(sp, id) => new StatefulAgentOptions { ... }` to `(sp, id, ct) => ValueTask.FromResult(new StatefulAgentOptions { ... })`.
- **Breaking:** `AiAgentGrain` constructor parameter `optionsFactory` type changed from `Func<string, StatefulAgentOptions>?` to `Func<string, CancellationToken, ValueTask<StatefulAgentOptions>>?`.
- `AiAgentGrain.OnActivateAsync` is now truly `async` — it awaits the options factory directly instead of blocking via `GetAwaiter().GetResult()`. This eliminates the Orleans grain activation deadlock that caused 2-minute `ResponseTimeout` failures on first invocation.

### Added (observability, continued)
- **Token counts and model on `python.agent.ask` spans.** `PythonAgentShim.AskAsync` sets `gen_ai.response.model`, `gen_ai.usage.input_tokens`, and `gen_ai.usage.output_tokens` on the `python.agent.ask` activity from `AgentInvokeResponse.Usage`. Langfuse can display token counts and calculate costs for Python agent invocations when the plugin returns usage data.
- **Researcher plugin returns token usage.** `langgraph-researcher-live/agent.py` now collects per-invocation token counts via a `_TokenUsageCallback` (LangChain `BaseCallbackHandler`) passed through `_compiled.invoke(config={"callbacks": [tracker]})`, and returns them as `AgentUsage` in `AgentResponse`. The callback accumulates `usage_metadata` from every `AIMessage` emitted by any node in the graph, so multi-node graphs are covered automatically.
- **P4-B: W3C traceparent propagation to Python agents.** `PythonAgentShim` now passes the W3C `traceparent` (the `python.agent.ask` span's Activity ID) in `AgentInvokeRequest.context["traceparent"]`. The `vais-agent-sdk` runner reads it on every `vais/agent.invoke` and `vais/agent.stream` call, calls `opentelemetry.context.attach()` before invoking the user callable, and `detach()` after, so all OTel spans emitted by the Python agent (LLM calls, LangGraph node transitions, tool calls) are automatically parented to the correct `python.agent.ask` span in Langfuse. `setup_otel()` is called once at process start; reads `OTEL_EXPORTER_OTLP_ENDPOINT` from the environment. `opentelemetry-sdk` and `opentelemetry-exporter-otlp-proto-http` added as required deps in `vais-agent-sdk/pyproject.toml` — picked up automatically by the Dockerfile `uv pip install /sdk` step.
- **`tool.call/{name}` spans for every tool invocation.** `DefaultToolCallDispatcher` now wraps `tool.InvokeAsync` in a child span of the ambient `chat` span, tagged with `vais.tool.name`, `vais.tool.call_id`, `gen_ai.prompt` (JSON arguments), and `gen_ai.completion` (result text). Error path sets `ActivityStatusCode.Error` and `error.type`. Reuses the existing `"Vais.Agents"` ActivitySource — no new source registration needed. Covers both SK and MAF backends: `MafCompletionProvider` uses `UseProvidedChatClientAsIs = true`, which disables MAF's `FunctionInvokingChatClient` middleware and routes tool calls through `StatefulAiAgent`'s dispatcher, same as SK. Full tree in Langfuse: `graph.run → graph.node → grain.ask → chat → tool.call/SearchWeb → …`
- `AgenticTags.ToolName` (`"vais.tool.name"`) and `AgenticTags.ToolCallId` (`"vais.tool.call_id"`) — new tag constants for tool-call spans.

### Fixed
- **`grain.activate` orphan traces in Langfuse.** `AiAgentGrain.OnActivateAsync` now only starts the `grain.activate` span when `Activity.Current != null`. Previously, Orleans always fired `OnActivateAsync` on the grain scheduler with no ambient trace context, producing a disconnected root trace per grain activation (four per pipeline run). Span is silently skipped when there is no parent.
- **Test spans polluting the demo Langfuse project.** Added `vais.runsettings` (repo root) that sets `OTEL_TRACES_EXPORTER=none`, `OTEL_METRICS_EXPORTER=none`, and `OTEL_LOGS_EXPORTER=none`. `Directory.Build.props` auto-wires this file for all `IsTestProject` projects via `<RunSettingsFilePath>`, so host-level `OTEL_EXPORTER_OTLP_ENDPOINT` env vars no longer leak into test runner processes.
- **`invoke-graph --stream` crashes on `StateUpdated` events.** `GraphEventRenderer` rendered changed-key names as `[key1, key2]` — Spectre.Console interpreted the brackets as a markup style tag and threw "Could not find color or style". Escaped to `[[key1, key2]]` so the keys render as plain text.
- **`finalState: null` in graph invoke response.** `InProcessGraphOrchestrator.StreamAsync` accumulates state in an internal copy that was never surfaced back to the caller. Added `IReadOnlyDictionary<string, JsonElement>? FinalState` to `GraphCompleted`; the orchestrator now snapshots the terminal state bag into the event. `DrainInvokeAsync` and `DrainResumeAsync` in `AgentGraphLifecycleManager` now capture `FinalState` from the event instead of returning the original (unchanged) initial state.
- **Plugin ABI mismatch log was not actionable.** `AssemblyPluginLoader.LoadViaAttribute` emitted a generic warning on version mismatch without telling the author how to fix it. Warning now includes the exact `[assembly: VaisPlugin(targetApiVersion: "...")]` attribute change and rebuild instruction needed to resolve the mismatch.
- **OTLP traces not sent to Langfuse.** Two root causes: (1) `CompositionRoot.ConfigureObservability` set `o.Endpoint` programmatically — when the endpoint is set in code the .NET OTEL SDK does NOT append the signal-specific path suffix (`/v1/traces`) and all export requests hit the base path which returns 404; (2) `OTEL_EXPORTER_OTLP_HEADERS` was not read explicitly, relying on the SDK's env-var auto-read which was blocked by the code-level configure action. Fix: removed `o.Endpoint` from the configure action (letting the SDK read `OTEL_EXPORTER_OTLP_ENDPOINT` and correctly append `/v1/traces`); added `OtelHeaders` to `RuntimeOptions` and explicitly set `o.Headers` in the configure action. Also added `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf` to `docker-compose.demo.yml` for clarity.
- **Orleans grain activation deadlock.** `OnActivateAsync` → `TranslateForGrain` → `GetAwaiter().GetResult()` blocked the Orleans single-threaded scheduler while waiting for an inner grain RPC whose continuation was posted back to that same blocked scheduler — deadlock held for exactly `SiloMessagingOptions.ResponseTimeout` (2 minutes). Root cause: `OrleansAgentRegistry` methods lacked `ConfigureAwait(false)` and `TranslateForGrain` used a sync-over-async bridge. Both issues resolved.
- Added `ConfigureAwait(false)` to four grain-to-grain calls in `OrleansAgentRegistry` (`ListAsync`, `GetAsync`, `RegisterAsync`) — independent fix that unblocks the deadlock even with the old sync bridge.

### Added (observability)
- `OrleansDiagnostics` — new public class in `Vais.Agents.Hosting.Orleans` exposing `ActivitySourceName = "Vais.Agents.Hosting.Orleans"` and a shared `ActivitySource` for Orleans grain spans.
- `AiAgentGrain` now emits `grain.activate` and `grain.ask` OTel spans tagged with `vais.agent.name`. Both spans are automatically picked up by `AddAgenticInstrumentation` — no consumer changes required.
- `AiAgentGrain` now logs structured events at `Debug`/`Information`/`Error` level (category `Vais.Agents.Hosting.Orleans.AiAgentGrain`): grain activating, options factory elapsed time, activation mode (`plugin`/`declarative`), and per-ask elapsed time. Factory exceptions are logged at `Error` with elapsed milliseconds — enough to distinguish an Orleans deadlock (factory blocked for ~120 000 ms) from a slow but successful cold-start. Enable `Debug` in appsettings to see per-method timing; `Information` is on by default.
- `AddAgenticInstrumentation(TracerProviderBuilder)` now also registers the `"Vais.Agents.Hosting.Orleans"` source in addition to `"Vais.Agents"`.
- **Langfuse observability enrichment.** Graph executions, agent I/O, and trace hierarchy are now visible in Langfuse:
  - `InProcessGraphOrchestrator` emits `graph.run` (root per invocation) and `graph.node` (one per node) OTel spans via a new `"Vais.Agents.Core.Graph"` `ActivitySource`. Tags: `graph.id`, `graph.version`, `graph.run_id`, `graph.entry`, `graph.node.id`, `graph.node.kind`, `vais.agent.name` (on Agent-kind nodes), `langfuse.trace.name`, `langfuse.session.id` (from `AgentContext.CorrelationId` / `UserId`).
  - `OrleansOutgoingActivityFilter` — new `IOutgoingGrainCallFilter` that writes the current W3C traceparent into `Orleans.RequestContext` before each grain call. `AiAgentGrain.AskAsync` reads it back and parents its `grain.ask` span to the caller's `graph.node` span. Result: Langfuse renders the full tree `graph.run → graph.node → grain.ask → chat`.
  - `StatefulAiAgent` `chat` span now carries `gen_ai.prompt` (user message) and `gen_ai.completion` (final assistant text), and `langfuse.observation.type=generation` so Langfuse renders it in its generation view with model, tokens, and cost.
  - `PythonAgentShim.AskAsync` now emits a `python.agent.ask` span (source `"Vais.Agents.Runtime.Plugins.Python"`) with `gen_ai.prompt` and `gen_ai.completion`. Registered via `AddAgenticInstrumentation`.
  - `OTEL_SERVICE_NAME=vais-oss-runtime` added to `docker-compose.demo.yml` — fixes `service.name: unknown_service:dotnet` in Langfuse resource attributes.

---

## [0.23.0-preview] — 2026-04-24

### Added
- **Python plugin pillar.** MCP stdio subprocess hosting for Python agents. Deploy a `pyproject.toml`-based Python package alongside the runtime; the host spawns it, handshakes via MCP `initialize` + `tools/list`, and restarts on crash with exponential backoff.
- `PythonSubprocessSupervisor` — per-plugin state machine: spawn → MCP handshake → Ready → restart loop. Configurable handshake timeout, invoke timeout, and restart policy (`Never` / `ExponentialBackoff`).
- `PythonPluginHostService` — `IHostedService` that scans the plugins directory, starts supervisors in parallel, and exposes `IPythonPluginHost` for introspection.
- `PythonPluginScanner` / `PluginYamlDeserializer` — discovers Python plugin packages via `pyproject.toml` `[tool.vais.plugin]` metadata.
- `PythonPluginDescriptor` record — captures plugin name, directory, interpreter path, entrypoint, ABI version, handshake/invoke timeouts, restart policy, declared tools, and secret refs.
- `INamedToolSourceProvider` — extension point for per-named-server `IToolSource` lookup. Implemented by `PythonPluginHostService` so MCP tool references in agent manifests resolve to the running subprocess client.
- `PythonPluginUrns` — structured URN constants for all plugin lifecycle events (`load-failed`, `handshake-timeout`, `exited`, `unavailable`, `abi-mismatch`, `ambiguous-folder`).
- ABI version negotiation: plugin declares `target_api_version` in `pyproject.toml`; host rejects mismatched versions with `abi-mismatch` URN.
- `AddPythonPlugins` DI extension wiring `PythonPluginHostService` + `INamedToolSourceProvider` into the silo.
- `PluginAgentResearchPlanner` sample — LangGraph-based research-planner Python agent deployed as a plugin, with `pyproject.toml`, venv setup, and `docker-compose` overlay.
- `PythonEchoWireTests` — wire-level integration tests for `PythonSubprocessSupervisor` including timeout, asyncio dispatch, and restart scenarios.
- Concept doc `docs/concepts/polyglot-agents.md` and guide `docs/guides/package-a-python-agent.md`.
- `ManifestInstantiationUrns.McpServerUnavailable` and `McpToolNotFound` URN constants.

### Changed
- `AiAgentGrainState` gains `OpaqueState` property for persisting plugin-agent opaque blobs across silo restarts.

---

## [0.21.0-preview] — 2026-04-22

### Added
- **Streaming journal replay** (`ReplayMode.Full`). `OrleansAgentJournal.ReadAsync` replays persisted `JournalEntry` records as an `IAsyncEnumerable<JournalEntry>`, including `CompletionDeltaRecorded` entries for streaming deltas.
- `CompletionDeltaRecorded` journal entry kind — records each streaming completion chunk with model id, prompt/completion tokens, and text delta.
- **Per-attempt retry telemetry.** `StatefulAiAgent`'s resilience pipeline now emits attempt-number tags on every retry so dashboards can bucket retries vs. first-try calls.
- **A2A cross-runtime graph nodes.** `RemoteAgentNode` resolves the target runtime URL from the graph manifest's `A2ARemoteAgents` declarations and calls it over the A2A wire protocol, enabling graph steps to fan out to agents in other Vais.Agents clusters or third-party A2A endpoints.
- **OIDC/OAuth 2.0 token exchange for cross-runtime identity propagation.** Three propagation modes: `Forward` (pass the inbound bearer token), `ServiceAccount` (use a pre-configured client-credential token), and `TokenExchange` (RFC 8693 subject-token exchange). Configured per `RemoteRuntime` entry in `Vais:RemoteRuntimes`.

---

## [0.20.0-preview] — 2026-04-21

### Added
- **Cross-runtime graph refs.** Agent graph manifests can reference agents on remote Vais.Agents runtimes (or any A2A-compatible endpoint). `runtimeUrl` field on `AgentNodeRef` selects the target cluster.
- `runtimeUrl` propagation through all manifest loaders (JSON, YAML, CRD schema).
- `AgentRemoteInvoker` service + typed HTTP client for outbound cross-runtime A2A calls.
- Concept doc + guide for cross-runtime graphs.

---

## [0.19.0-preview] — 2026-04-21

### Added
- **Agent graph as a first-class deployable.** `AgentGraphManifest` can be registered via `vais apply -f` and stored in Orleans grain storage (`OrleansAgentGraphRegistry`) so graph definitions survive silo restart.
- `IAgentGraphRegistry` / `OrleansAgentGraphRegistry` — durable graph manifest storage, mirroring `IAgentRegistry` for agent manifests.
- CRD schema for `AgentGraph` Kubernetes custom resource.
- Helm chart additions for graph registry grain storage.

---

## [0.18.0-preview] — 2026-04-21

### Added
- **Plugin loader wired into `Runtime.Host`.** `CompositionRoot` now discovers and loads code-authored plugin assemblies (`.dll` files in a configurable plugin directory) at startup via `AssemblyLoadContext` isolation.
- `PythonPluginsDirectory` runtime option — enables the Python plugin scan path when set.
- Plugin agent factory registration flows through `IPluginHandlerRegistry` into `AgentManifestTranslator`.

---

## [0.17.0-preview] — 2026-04-21

### Added
- **Declarative agent translator (Pillar B).** `AgentManifestTranslator` translates a stored `AgentManifest` into `StatefulAgentOptions`, resolving model provider, system prompt (inline / template-ref / file-ref), static tools, MCP tools, A2A remote agents, and guardrails.
- `IAgentManifestTranslator` — interface with `TranslateAsync` + `TranslateForGrain` + `InvalidateAsync`.
- `ICompletionProviderPool` — memoised provider pool; shares a single SDK client across all activations of the same `ModelSpec`.
- Per-agent provider resolution: each agent grain gets the provider declared in its manifest's `ModelSpec` rather than a silo-wide singleton.
- `ConfigureAgentGrains` extension wired to the translator in `CompositionRoot` — grain activation now reads the manifest and instantiates the correct provider automatically.
- Builtin guardrail factories: `LengthCap`, `RegexAllowlist`, `RegexDenylist` (input), `LlmAsJudge` (output).
- `FileSystemPromptFileLoader` and `IPromptTemplateRegistry` for system-prompt resolution.
- `ManifestInstantiationUrns` — structured error URNs for all translation failure modes.
- `TranslatorInvalidationHook` — invalidates the translator cache on `UpdateAsync` / `EvictAsync` so the next grain activation picks up the new manifest.

### Changed
- `AiAgentGrain` now derives its `ICompletionProvider` from the per-agent options supplied by the translator (v0.17 Pillar B wire-through) rather than requiring a silo-wide `ICompletionProvider` registration.

---

## [0.16.0-preview] — 2026-04-21

### Added
- **`Vais.Agents.Runtime.Host`** — the deployable runtime container entrypoint. Hosts an Orleans silo, exposes the agent control-plane HTTP API, and wires all pillars together via `CompositionRoot`.
- `RuntimeOptions` — typed configuration model for the runtime container (Orleans connection strings, plugin directories, remote runtime URLs, OPA endpoint, etc.).
- Docker image build (`src/Vais.Agents.Runtime.Host/Dockerfile`).
- `docker-compose.localhost.yml` base recipe + `opa`, `langfuse`, `otel`, `clustered` overlays.
- Helm chart `deploy/helm/vais-agents-runtime/`.
- `CONTRIBUTING.md`, `SECURITY.md`, `CODE_OF_CONDUCT.md`.

---

## [0.15.0-preview] — 2026-04-20

### Added
- **`vais` CLI** (`Vais.Agents.Cli`). Subcommands: `apply`, `get`, `delete`, `invoke`, `logs`, `graph apply/get/delete/invoke`.
- `vais apply -f <manifest.yaml>` — deploy an agent or graph manifest to a running runtime.
- `vais invoke <agent-id> "<message>"` — send a chat turn and stream the reply.
- Shell completions for `bash`, `zsh`, `fish`, `PowerShell`.
- `docs/reference/cli.md` — full subcommand reference.

---

## [0.14.0-preview] — 2026-04-20

### Added
- **OPA policy-engine pillar.** `IAgentPolicyEngine` backed by Open Policy Agent; evaluates `allow` decisions for every agent invocation, tool call, and graph step.
- `OpaAgentPolicyEngine` — HTTP client to an OPA sidecar; bundles the built-in Rego policy bundle.
- `AgentLifecycleManager` policy enforcement: `CreateAsync` / `UpdateAsync` / `EvictAsync` check `allow` before mutating registry state.
- `AddOpaAgentPolicyEngine` DI extension; `OpaOptions` configuration model.
- `docs/concepts/policy.md` and Helm sidecar values for OPA.

---

## [0.13.0-preview] — 2026-04-20

### Added
- **Kubernetes operator pillar** (`Vais.Agents.Control.KubernetesOperator.Host`). Watches `AgentManifest` and `AgentGraph` CRDs; reconciles the agent registry and graph registry to match declared state.
- `AgentManifestReconciler` and `AgentGraphReconciler` — idempotent reconcile loops using KubeOps.
- CRD YAML definitions for `AgentManifest` and `AgentGraph` (v1alpha1).
- Helm chart `deploy/helm/vais-agents-operator/`.
- `deploy/crds/` — standalone CRD install for non-Helm environments.
- `docs/guides/deploy-the-operator.md`.

---

## [0.12.0-preview] — 2026-04-20

### Added
- **SSE streaming invoke** (`POST /v1/agents/{id}/invoke/stream`). Server-Sent Events endpoint that streams `delta` events as the agent generates tokens, followed by a `done` event.
- `StreamAsync` on `IAiAgent` / `StatefulAiAgent` — yields `AgentStreamChunk` items; backed by provider-native streaming (SK / MAF).
- Streaming wire format: newline-delimited `data: {...}` events per SSE spec.
- `OrleansAgentRuntime.StreamAsync` grain method with `GrainCancellationToken` for SSE disconnect propagation.

---

## [0.11.0-preview] — 2026-04-20

### Added
- **OpenAPI spec** auto-generated from the HTTP control-plane endpoints; exposed at `/openapi/v1.json` and `/swagger`.
- **Idempotency-Key middleware** (`X-Idempotency-Key` header). Duplicate requests within the TTL window return the cached response; in-flight duplicates wait and share the result.
- `IIdempotencyStore` + `OrleansIdempotencyStore` — durable idempotency record storage backed by `IIdempotencyKeyGrain`.
- `AddOrleansIdempotencyStore` DI extension; `IdempotencyOptions` configuration model.
- `docs/reference/problem-details-urns.md` — full URN taxonomy for all Problem Details error types.

---

## [0.10.0-preview] — 2026-04-20

### Added
- **Filter + resilience pipeline on `StreamAsync`.** `IAgentFilter`, `IInputGuardrail`, `IOutputGuardrail`, and `IToolGuardrail` now apply to streaming paths as well as request/response.
- `Polly`-backed resilience pipeline on `StatefulAiAgent` — configurable retry, circuit-breaker, and timeout policies via `StatefulAgentOptions.ResiliencePipeline`.
- `IUsageSink` — callback for token usage reporting per turn; receives prompt + completion counts.
- `AgentBudget` — `MaxTurns` and `MaxTokens` hard caps enforced inside the agent loop; raises `BudgetExceededException`.

---

## [0.9.0-preview] — 2026-04-20

### Added
- **Agent graph orchestration pillar.** `IAgentGraph<TState>` — a DAG of agent nodes with conditional routing, parallel fan-out, and human-in-the-loop interrupt/resume.
- `InProcessGraphOrchestrator` — runs a graph to completion or to an `Interrupt` in-process; persists checkpoints via `IGraphCheckpointer`.
- `OrleansCheckpointer` — durable graph checkpoint storage backed by `IGraphCheckpointGrain`.
- `AgentGraphManifest` — YAML/JSON declarative format for graph topology.
- `GraphInterrupted` / `ResumeAsync` for human-in-the-loop patterns.
- `AddOrleansGraphCheckpointer` DI extension.
- `docs/concepts/graphs.md`.

---

## [0.8.0-preview] — 2026-04-19

### Added
- **A2A inbound server pillar.** `AddA2AAgentServer` mounts a standards-compliant [Agent-to-Agent (A2A) protocol](https://github.com/google-a2a/A2A) endpoint on the HTTP server; any A2A-capable client can discover and invoke registered agents.
- `OrleansTaskStore` — durable A2A task storage (`ITaskStore`) backed by `IA2ATaskGrain`; tasks survive silo restart.
- `AddOrleansA2ATaskStore` DI extension.
- `docs/concepts/a2a.md` and interoperability guide.

---

## [0.7.0-preview] — 2026-04-19

### Added
- **MCP server pillar** (`Vais.Agents.McpServer`). Exposes registered agents as MCP tools over stdio transport, streamable-HTTP transport, or both simultaneously.
- JWT dual-headed auth: accepts both MCP-native `Authorization` headers and the control-plane's existing JWT bearer tokens.
- `list_resources` / `read_resource` MCP endpoints — agents as named resources with history content.
- `McpAgentServer` builder + `AddMcpAgentServer` DI extension.
- `docs/concepts/mcp-server.md`.

---

## [0.6.0-preview] — 2026-04-19

### Added
- **Agent control-plane HTTP server** (`Vais.Agents.Runtime.Server`). REST endpoints: `POST /v1/agents` (apply), `GET /v1/agents/{id}`, `DELETE /v1/agents/{id}`, `POST /v1/agents/{id}/invoke`, `GET /v1/agents` (list), and graph equivalents.
- JWT authentication middleware; `IAgentContextAccessor` extracts tenant/user/correlation from the token into `AgentContext`.
- `IAuditLog` + `LoggerAuditLog` — structured audit events for every create/invoke/delete lifecycle step.
- JSON and YAML manifest loaders (`IManifestLoader`) with shared JSON-Schema validation.
- `AgentLifecycleManager` — orchestrates registry + runtime + policy across create/update/evict.
- Problem Details error shaping with `urn:vais-agents:*` URN types for all failure modes.
- Typed HTTP client (`AgentControlPlaneClient`) for .NET consumers of the control-plane API.

---

## [0.5.0-preview] — 2026-04-19

### Added
- **Durable journal pillar.** `IAgentJournal` records every tool call (name, arguments, result, call-id, timestamp) to a persistent log; `OrleansAgentJournal` is the Orleans-backed implementation.
- `IAgentRunJournalGrain` / `AgentRunJournalGrain` — grain storing `JournalEntry` records per run-id.
- `RunId` stamped on every agent run; threaded through `DefaultToolCallDispatcher` so all tool calls in a run share the id.
- `ResumeAsync(runId)` — restores the agent to its end-of-turn state from a prior run by replaying the journal.
- `AddOrleansAgentJournal` DI extension; `OrleansAgentJournal` wired into `CompositionRoot`.

---

## [0.4.0-preview] — 2026-04-19

### Added
- Initial public documentation set (`docs/`) covering concepts, getting-started guides, reference pages, ADRs, and roadmap.
- 20 samples under `samples/` covering every pillar through v0.4.
- `samples/README.md` learning-path table.

---

## [0.3.0-preview] — 2026-04-19

### Added
- Package rename: all packages migrated from `Vais2.Agents.*` to `Vais.Agents.*` namespace.
- `Vais.Agents.Abstractions` — core contracts: `IAiAgent`, `IAgentRuntime`, `IAgentRegistry`, `IAgentSession`, `ChatTurn`, `AgentManifest`, and all extension-point interfaces.
- `Vais.Agents.Core` — `StatefulAiAgent`, `InMemoryAgentRuntime`, `InMemoryAgentRegistry`, `DefaultToolCallDispatcher`.
- `Vais.Agents.Hosting.Orleans` — `AiAgentGrain`, `OrleansAgentRuntime`, `OrleansAgentRegistry`, `OrleansAgentSession`, and all supporting grains and surrogates.
- `Vais.Agents.Ai.SemanticKernel` + `Vais.Agents.Ai.MicrosoftAgentFramework` — completion provider adapters.
- `Microsoft.CodeAnalysis.PublicApiAnalyzers` enabled across all packable projects.
- Central Package Management (`Directory.Packages.props`).
- `AGENTS.md` — AI assistant briefing for this repository.
