# Research: Extract Actor-Agents as OSS Library + Cloud Runtime, add Microsoft Agent Framework support

**Status:** Research / scoping. Not yet a formal `/specify` feature.
**Author context:** See `PROJECT_SUMMARY.md` §4 for the actor-agent framework.
**Goal:** Spin the `Vais2Agents.*` subsystem out of the VAIS2 monorepo into
1. an **open-source library** (NuGet packages),
2. a **managed cloud runtime** that hosts user-defined agents,
3. with **dual AI-stack support**: Microsoft **Semantic Kernel** (current) **and** Microsoft **Agent Framework** (new).

This document catalogues the work, risks, and open questions; it is intentionally long but is organised as task lists that can feed a later `/specify` + `/plan`.

---

## 1. Current state recap (what we're extracting)

Three projects under `Backend/Actors/`:

| Project | Public surface | External deps |
|---|---|---|
| `Vais2Agents.Base` | `IAgent`, `IAiAgent`, `IMultiAgent`, `AgentEvent`, `AgentState<T>`, `ChatHistoryItem`, `ChatUserType` | `Microsoft.Orleans.Serialization.Abstractions`, `Microsoft.SemanticKernel`, `Microsoft.SemanticKernel.Agents.Core` |
| `Vais2Agents.Orleans` | `Agent`, `AiAgent<T>`, `MultiAgent`, `EventSurrogate`, `LangfuseEnrichmentFilter`, `Resolvers`, `ResourceReader`, `Extensions/` | `Microsoft.Orleans.Sdk`, `Microsoft.Orleans.Streaming`, `Microsoft.SemanticKernel`, `Microsoft.SemanticKernel.Connectors.OpenAI`, **project refs** → `SemanticKernelPooling`, `BackendServiceInterfaces`, `TokenUsageData` |
| `Vais2Agents.Tools` | `DuckDuckGoSearch` (example tool) | — |

Concrete agents under `Backend/Editor/Agents/` (`TechnicalAnalyst`, `FlowGenerator`, `FlowToNLAgent`, etc.) are **consumers**, not part of the library — they stay in VAIS2.

### 1.1 Inventory of coupling that must be broken before OSS

- [ ] **Contracts leak SK types** — `IAiAgent.CallFunction` takes `KernelArguments` + `OpenAIPromptExecutionSettings`; `IMultiAgent.AddAgent(ChatCompletionAgent)` — these force every caller to depend on SK, and exclude MAF.
- [ ] **Contracts leak Orleans types** — `Vais2Agents.Base` references `Microsoft.Orleans.Serialization.Abstractions`; OSS Base should be Orleans-free so non-Orleans implementations are possible.
- [ ] **Internal project refs from Orleans package** — `SemanticKernelPooling`, `BackendServiceInterfaces`, `TokenUsageData` are VAIS2-internal and must be either (a) published, (b) abstracted behind extension interfaces, or (c) removed.
- [x] **`LangfuseEnrichmentFilter`** is vendor-specific — ship it as an opt-in plug-in package, not in the core. *(M2b: shipped as `Vais2.Agents.Observability.Langfuse`, a neutral `IAgentFilter` that reads `IAgentContextAccessor` instead of Orleans `RequestContext`.)*
- [ ] **`ITokenUsageTracker` + `TokenUsageRequest`** are VAIS2-specific DTOs — replace with a generic `IUsageSink` / `UsageRecord` abstraction.
- [ ] **`IAgentPluginManager`** is VAIS2-specific — either publish the interface or replace with a simple `Func<Kernel,Task>` plugin configurator.
- [ ] **`RequestContext.Get("UserId"/"ProjectId"/"FlowId")`** ties tracing to Orleans' ambient request context and to VAIS2 semantics — replace with an injected `IAgentContextAccessor`.
- [ ] **Hard-coded retry (3×, exponential)** — surface as a policy (`IRetryPolicy` or Polly `ResiliencePipeline`).
- [ ] **Hard-coded prompt defaults** (`MaxTokens=4096`, `Temperature=0`, `TopP=1`, `gpt-5` quirks) — move into a `DefaultCompletionSettings` type or resolver.
- [ ] **`SemanticKernelPooling`** is itself a separate ThirdParty project — OSS strategy must decide whether to publish it, replace it (MAF has its own client caching model), or vendor a slimmed-down version.

---

## 2. Target architecture

Proposed package layout (working names — to be negotiated):

```
Agentic/                                  (repo root, new OSS repo)
├── src/
│   ├── Agentic.Abstractions/             # IAgent, IAiAgent, IMultiAgent, IAgentRuntime,
│   │                                     # IAgentState, IUsageSink, IAgentContextAccessor
│   ├── Agentic.Core/                     # Default in-memory impls, base classes, retries,
│   │                                     # event-bus contract, prompt & tool registries
│   ├── Agentic.Hosting.Orleans/          # Orleans-based virtual-actor host (today's code,
│   │                                     # decoupled from VAIS2-specific types)
│   ├── Agentic.Hosting.InMemory/         # Lightweight single-process host for tests / CLI
│   ├── Agentic.Ai.SemanticKernel/        # SK adapter: ISK KernelExecutor, SK filter bridge,
│   │                                     # SK-based IMultiAgent (AgentGroupChat)
│   ├── Agentic.Ai.MicrosoftAgentFramework/# MAF adapter: wraps MAF Agent / AgentThread,
│   │                                     # orchestration, tools, checkpointing
│   ├── Agentic.Tools/                    # Reusable tools (DuckDuckGo, HTTP, etc.)
│   ├── Agentic.Observability.OpenTelemetry/  # OTel wiring + gen_ai semantic conventions
│   ├── Agentic.Observability.Langfuse/   # Opt-in Langfuse enrichment (today's filter)
│   ├── Agentic.Persistence.Redis/        # Redis grain storage + stream provider
│   ├── Agentic.Persistence.Postgres/     # Postgres grain storage + history store
│   └── Agentic.Runtime.Client/           # SDK for calling the cloud runtime (REST/gRPC)
├── runtime/                              # Cloud runtime service (Docker + Helm)
│   ├── Agentic.Runtime.Api/              # ASP.NET host: agent registry, invocation API
│   ├── Agentic.Runtime.Silo/             # Orleans silo hosting tenant agents
│   └── Agentic.Runtime.Cli/              # `agentic` CLI (deploy / invoke / logs)
├── samples/
├── tests/
└── docs/                                 # docfx / docusaurus site
```

### 2.1 Key abstractions to design

- [ ] **`IAgentRuntime`** — top-level façade for "get me an agent by id/type", hides whether it's Orleans-backed, in-process, or remote.
- [ ] **`IAgent`** — unchanged minimum: event in / event out.
- [ ] **`IAiAgent`** (redesigned, stack-neutral):
      - `Task<AgentResponse> InvokeAsync(AgentInvocation input, CancellationToken ct)`
      - `IAsyncEnumerable<AgentEvent> StreamAsync(AgentInvocation input, CancellationToken ct)` (streaming)
      - No `KernelArguments`; use `AgentInvocation { string Prompt, Dictionary<string,object> Arguments, CompletionSettings Settings, IList<ITool> Tools }`.
- [ ] **`IMultiAgentOrchestrator`** — replaces `IMultiAgent`. Hosts a group of `IAiAgent`s plus a `TerminationPolicy` and `SelectionPolicy` that are framework-agnostic (SK's `TerminationStrategy` and MAF's equivalents map to the same abstraction).
- [ ] **`ICompletionProvider`** — pluggable "call an LLM once" contract; SK and MAF each supply an implementation. This is the point where the two frameworks diverge in code, everywhere above is shared.
- [ ] **`IAgentState<T>` / `IAgentStateStore`** — swap Orleans' `IPersistentState<T>` for something that maps to Orleans when hosted there, or to Redis/SQL directly otherwise.
- [ ] **`IAgentEventBus`** — today: Orleans streams. Needs a generic façade so in-memory / Kafka / NATS can swap in.
- [ ] **`IUsageSink`** + `UsageRecord` — generic token / cost sink. VAIS2 provides a bridge to its existing `ITokenUsageTracker`.
- [ ] **`IAgentContextAccessor`** — structured equivalent of `RequestContext.Get("UserId")`; values are carried via `AsyncLocal` or the host's ambient context.
- [ ] **`IToolRegistry` / `ITool`** — MAF has a first-class tool model; SK has plugins. Unify at this layer.

### 2.2 Two adapter stacks

- [ ] **SK adapter** (`Agentic.Ai.SemanticKernel`)
      - Implements `ICompletionProvider` using `Kernel.CreateFunctionFromPrompt` + `InvokeAsync`.
      - Brings across (or replaces) `SemanticKernelPooling` for multi-key / multi-provider balancing.
      - Implements `IMultiAgentOrchestrator` via `AgentGroupChat`.
      - Bridges tools → SK plugins (`KernelFunctionFactory`).
- [ ] **MAF adapter** (`Agentic.Ai.MicrosoftAgentFramework`)
      - Implements `ICompletionProvider` via MAF `ChatClientAgent` / `AIAgent.RunAsync`.
      - Uses MAF's own `AgentThread` for per-invocation state; bridges to our `IAgentState<T>` (thread snapshot ↔ chat history).
      - Uses MAF's orchestration primitives (Sequential / Concurrent / GroupChat / Handoff) for `IMultiAgentOrchestrator`.
      - Bridges tools → MAF `AIFunction` (from `Microsoft.Extensions.AI`).
      - Requires a research subtask: MAF is newer and less stable — pin versions explicitly.

### 2.3 Why a thin shared core matters

The business value of the library is **not** "another SK wrapper" — it is **Orleans-backed, durable, multi-tenant agent hosting**. Both SK and MAF are "call the LLM" mechanics. Everything around that (identity, persistence, streams, multi-agent sessions, usage tracking, RAG injection, event bus) is what we own and what we publish.

---

## 3. Tasks grouped by workstream

### W1 — ~~Decoupling & refactor in VAIS2~~ **DEFERRED — out of scope for now**

> **Scope decision:** VAIS2 is **not** being touched as part of this effort. All decoupling work happens inside the new OSS repo, starting from a copy of the current `Vais2Agents.*` code and rewriting it to remove VAIS2-internal dependencies. VAIS2 keeps using its existing internal `Vais2Agents.*` projects unchanged for now.
>
> **Why:** the OSS repo can prove the abstractions (SK + MAF adapters, `IChatClient`-based pooling, neutral `IUsageSink`, etc.) against its own tests and samples, without the risk / coordination cost of refactoring a live production system in parallel. VAIS2 will migrate to the OSS packages later as a separate piece of work (see W9, also deferred).
>
> The bullets below are preserved for context — they describe decoupling work that **still must happen, but now inside the OSS repo in W3**, not inside VAIS2:
>
> - ~~Audit references to `Vais2Agents.*` in VAIS2~~ → deferred to W9.
> - Replace SK-typed contract signatures with neutral types (`AgentInvocation`, `CompletionSettings`, etc.) → **done in W3** on the OSS side, against a fresh codebase.
> - Replace `RequestContext.Get("UserId"/"ProjectId"/"FlowId")` with `IAgentContextAccessor` → **done in W3**; a VAIS2-side `RequestContext`-reading implementation of the accessor is written later, in W9.
> - Replace `ITokenUsageTracker` with `IUsageSink` → **done in W3**; VAIS2's adapter sink is written later, in W9.
> - Replace `IAgentPluginManager` with a neutral configurator → **done in W3**.
> - Langfuse filter → **authored fresh** in `Agentic.Observability.Langfuse` (W6). VAIS2's copy stays put until W9.
> - `SemanticKernelPooling` → **reimplemented fresh** as keyed `IChatClient` + `LoadBalancingChatClient` in the OSS repo (W4). VAIS2's copy stays put until W9.
> - `[Obsolete]` shims in VAIS2 → **not needed** in this effort; VAIS2's code is untouched.

### W2 — New OSS repo scaffolding

- [x] **License: Apache 2.0 across library and cloud runtime** (decision §4.3). Patent grant included; no BSL/SSPL.
- [ ] Choose governance model (single-vendor vs community from day one).
- [x] Create the new standalone repository (decision §4.1). **Lives at `oss/agentic/` as its own git repo inside VAIS2; will move to GitHub when the trademark/NuGet-name pass in W10 clears.** Contains:
  - [x] `README.md`, `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, `SECURITY.md`.
  - [x] `.editorconfig`, `.gitignore`, Directory.Build.props for common settings.
  - [x] Central package versioning (`Directory.Packages.props`).
  - [ ] Issue / PR templates, CODEOWNERS. *(deferred to M2)*
- [x] CI (GitHub Actions): build + test on ubuntu + windows. *(coverage / nuget pack / signing deferred to M2.)*
- [ ] Set up release pipeline: Nerdbank.GitVersioning or MinVer; publish to NuGet.org on tag. *(deferred — not publishing yet.)*
- [ ] Set up a docs site (docfx for API reference, plus conceptual docs). Host on GitHub Pages. *(deferred to M2.)*
- [x] Pick namespace and NuGet package-prefix. **Working prefix: `Vais2.Agents.*`. Explicitly a placeholder pending W10 trademark / existing-NuGet-package clearance.**

### W3 — Core library extraction (**absorbs all decoupling work since W1 is deferred**)

Since VAIS2 is not being modified, W3 is where the code is copied in *and* decoupled in a single pass. The OSS repo's tests are the correctness contract, not VAIS2's.

Copy + restructure:
- [x] Copy `Vais2Agents.Base` → **`Vais2.Agents.Abstractions`**, dropping Orleans and SK references (M1).
- [x] Copy `Vais2Agents.Orleans` → `Vais2.Agents.Hosting.Orleans`, referencing `Vais2.Agents.Abstractions` only; the SK and MAF adapters plug in at runtime via DI. **M3a landed:** `IAiAgentGrain` + sealed `AiAgentGrain` wrap the neutral `StatefulAiAgent` and persist `(History, SystemPrompt)` via `IPersistentState<AiAgentGrainState>`. `OrleansAgentRuntime : IAgentRuntime` + internal `OrleansAiAgentProxy` bridge the grain into the stack-neutral `IAiAgent` surface. `OrleansAgentContextAccessor` reads `RequestContext` under the same `vais2.*` keys as the OTel tags. Surrogates (`ChatTurnSurrogate`, `AgentContextSurrogate`) keep Abstractions Orleans-free. Event-stream / multi-agent / `IAgent` stream-base grain deferred to M3e. 12-test TestingHost fixture.
- [ ] Copy `Vais2Agents.Tools` → `Vais2.Agents.Tools`; split per tool if dependencies diverge. *(Still deferred — M2c shipped the `ITool` contract and bindings. Packaging built-in tools like DuckDuckGo lands when a consumer needs them; contract alone was the blocker.)*

Decoupling that used to live in W1 (do it all here, against neutral stubs / fakes in tests):
- [x] Define neutral contracts. **M1 landed:** `ICompletionProvider`, `CompletionRequest`, `CompletionResponse`, `IAiAgent`, `ChatRole`, `ChatTurn`. **M2a added:** `IUsageSink` + `UsageRecord`, `IAgentContextAccessor` + `AgentContext`, `IAgentFilter`, `IAgentRuntime`. **M2c added:** `ITool` + `IToolRegistry` plus `CompletionRequest.Tools`. **Still owed (M3):** `IKernelConfigurator` / `IAgentConfigurator`, `IAgentEventBus`, `IAgentStateStore`.
- [x] Rewrite `AiAgent<T>.CallFunction` so the neutral core owns retries, filter attach, usage capture, history/state persistence. **M2a:** `StatefulAiAgent` runs every turn through a `Microsoft.Extensions.Resilience` pipeline → ordered `IAgentFilter` chain → `ICompletionProvider`, with `IUsageSink` emission on both success and failure and `IAgentContextAccessor` feeding `UsageRecord` fields. Default resilience: 3 attempts, exponential back-off + jitter, cancellation passes through unretried. Persistence hooks (saving grain state, chat-history stores) still owed for M3 when Orleans host lands.
- [x] Replace `RequestContext.Get(...)` reads with `IAgentContextAccessor` calls. M2a shipped `AsyncLocalAgentContextAccessor` (works across awaits, Push/restore scoping). **M3a shipped `OrleansAgentContextAccessor`** (reads `RequestContext` under `AgenticTags.*` keys so ingress-side `RequestContext.Set(AgenticTags.UserId, ...)` surfaces on both `AgentContext` and the turn's OTel activity).
- [x] Replace `ITokenUsageTracker` calls with `IUsageSink`. M2a shipped `NullUsageSink` as the default. `OpenTelemetryUsageSink` + Langfuse sink land in M2b (observability package).
- [ ] Replace `IAgentPluginManager` dependency with `IKernelConfigurator` (SK-side) / `IAgentConfigurator` (MAF-side). Default impls are empty; samples demonstrate wiring. *(Deferred to M3 — M2c shipped `ITool` + `IToolRegistry` instead, which is the smaller, stack-neutral surface that actually matters for tool-calling. Per-adapter kernel/agent configurators become necessary only once consumers need to inject custom SK filters or MAF middleware, which isn't a Phase-1 requirement.)*
- [x] Provide a default `InMemoryAgentRuntime` for tests and single-process usage. M2a shipped `Vais2.Agents.Hosting.InMemory` with `InMemoryAgentRuntime` (`ConcurrentDictionary`-backed) and `AddInMemoryAgentRuntime()` DI extension.
- [ ] Test harness: the same scenarios run on Orleans TestingHost and on the in-memory host, producing byte-identical outputs. *(M1 shipped 11 unit tests; M2a raised to 22; M3a added 12 Orleans TestingHost tests — activation, history retention across deactivation, system-prompt persistence, reset, delete-and-rehydrate, runtime GetOrCreate / TryGet / Remove, context-accessor round-trip. Cross-host equivalence harness — run the same scenario on InMemory and Orleans, compare outputs — still deferred to M3e.)*

API hygiene:
- [x] XML doc comments on every public type.
- [x] Enable Roslyn public-API analyzer (`Microsoft.CodeAnalysis.PublicApiAnalyzers`). **M2a flipped RS0016/RS0017/RS0025/RS0026/RS0037 to warning** and baselined **171 entries** across 5 src projects into `PublicAPI.Unshipped.txt` (128 Abstractions, 28 Core, 4 SK adapter, 4 MAF adapter, 7 Hosting.InMemory). Future surface changes fail the build until declared.
- [x] Version `Vais2.Agents.Abstractions` conservatively — it is the surface most users pin. **API-freeze sweep done 2026-04-18**: all seven packages moved their Phase-1 surface from `PublicAPI.Unshipped.txt` → `PublicAPI.Shipped.txt` (Abstractions 139 / Core 51 / SK 4 / MAF 5 / InMemory 8 / OTel 9 / Langfuse 27 ≈ 243 entries shipped). Build + 38/38 tests green. Ready for `0.1.0-preview` tag once the `HelloAgent` dog-food pass completes.

### W4 — Semantic Kernel adapter (positioned as the **alternative** stack; MAF is primary per §4.4)

- [x] **`Vais2.Agents.Ai.SemanticKernel.SkCompletionProvider`** ships (M1). It uses SK's **native `IChatCompletionService`** path rather than bridging through `IChatClient`, so the SK-specific machinery (`ChatHistory`, `OpenAIPromptExecutionSettings`, `ChatTokenUsage` metadata) is exercised against the neutral abstraction.
- [ ] Port `LangfuseEnrichmentFilter` to a generic `IAgenticCompletionFilter` concept, with Langfuse shipped as a consumer. *(Deferred to W6 / M2.)*
- [ ] Port `AgentGroupChat`-based multi-agent orchestrator. *(Deferred — multi-agent comes with M2.)*
- [x] **`AddKnowledge` ported to `Microsoft.Extensions.VectorData`** (decision §4.7). M3d shipped `Vais2.Agents.Persistence.VectorData` with a neutral `IKnowledgeRetriever` + `KnowledgeChunk` contract in Abstractions, a `VectorStoreKnowledgeRetriever<TKey, TRecord>` over `Microsoft.Extensions.VectorData.Abstractions` + `IEmbeddingGenerator<string, Embedding<float>>`, and a `KnowledgeRetrievalFilter : IAgentFilter` that augments each turn's system prompt with retrieved chunks (retrieved context never enters `IAiAgent.History`). Works for both SK and MAF adapters because the retrieval happens in the filter pipeline above the `ICompletionProvider` — stack-agnostic.
- [x] Pin minimum SK version (1.62.0) and document the experimental flags (`SKEXP0001`, `SKEXP0010`, `SKEXP0110`, `MEAI001`) we depend on — all in `oss/agentic/Directory.Build.props`.
- [x] SK samples in docs are labelled as "alternative stack"; every SK sample has a matching MAF sample. **M1's `HelloAgent` runs both sequentially** with the stacks clearly labelled in output.

### W5 — Microsoft Agent Framework adapter (**primary** stack per §4.4; shipped in parallel with W4)

MAF (`Microsoft.Agents.AI.*`) is the unified successor to AutoGen + SK agents, GA'd in late 2025. It introduces:
- `AIAgent`, `ChatClientAgent`, `AgentThread` (stateful conversation), `AgentRun`, streaming.
- `Microsoft.Extensions.AI.IChatClient` as the LLM abstraction (replaces SK's Kernel in MAF's world).
- Orchestration patterns (`Sequential`, `Concurrent`, `GroupChat`, `Handoff`).
- Native tool model via `AIFunction`.
- Workflow / graph runtime (`Microsoft.Agents.AI.Workflows`).
- Durability hooks (`AgentThreadState` serialisation).

Research + build tasks:

- [x] **Research**: current stable package IDs and versions on NuGet. Findings in the Phase-0 spike README; pinned in `oss/agentic/Directory.Packages.props`: `Microsoft.Agents.AI 1.0.0-preview.251009.1` (rc2 exists in Microsoft Learn but we're on preview until we need it), `Microsoft.Extensions.AI 9.10.0`, `Microsoft.Extensions.AI.OpenAI 9.10.0-preview.1.25513.3`.
- [ ] **Research**: MAF's checkpointing / thread-state serialisation API (`AIAgent.SerializeSessionAsync` / `DeserializeSessionAsync` per API reference) — noted in M1's Phase-0 findings, but deep dive + prototype deferred to M3 when Orleans grain persistence lands.
- [x] **Research**: MAF's tool/function model and how it composes with `Microsoft.Extensions.AI.AIFunction`. Map to our `ITool`. *(M2c — `MafToolBinder` adapts `ITool` → MEAI `AIFunction`; MAF's `FunctionInvokingChatClient` pipeline layer handles auto-invocation. Same bridge reused on the SK side via `AIFunction.AsKernelFunction()`.)*
- [x] **Research**: MAF ↔ A2A (Agent-to-Agent) protocol support. **Found**: MAF ships native A2A exposure via the `AIAgent.MapA2A(...)` extension method (`Microsoft.Agents.AI.Hosting.A2A.AIAgentExtensions`). **Consequence**: the cloud runtime in Phase 3 does not need to write an A2A protocol bridge — it lifts the spec revision MAF targets.
- [x] **Design**: grain owns identity + per-agent chat history; MAF owns per-invocation reasoning; grain serialises `AgentSession` state via MAF's session serializer when durability is needed. **Confirmed by M1's `MafCompletionProvider`** which deliberately passes `thread: null` and relies on the neutral `StatefulAiAgent` for history — validates the design end-to-end.
- [x] **Build**: `MafCompletionProvider : ICompletionProvider` wrapping `ChatClientAgent` via `IChatClient.CreateAIAgent(...)`. M1.
- [ ] **Build**: `MafMultiAgentOrchestrator : IMultiAgentOrchestrator` using MAF orchestration primitives. *(M2.)*
- [ ] **Build**: `MafAgentState` — adapts `AgentSession` (was `AgentThread`) to `IAgentState<T>`. *(M3, with Orleans host.)*
- [ ] **Build**: bridge so MAF's OTel conventions (`gen_ai.*`) plug into our observability package. *(W6 / M2.)*
- [x] **Parity tests**: every scenario has both an SK-backed and an MAF-backed test producing equivalent behaviour. **M2c landed `tests/Vais2.Agents.ParityTests` (5 tests, all green)** covering tool-calling binding identity (same name/description/schema on both sides) and bridge invocation (ITool invoked with matching JSON args through either adapter's function-bridge type). Chat-history retention has informal parity via `HelloAgent`. Multi-agent termination and streaming parity land in M3.

### W6 — Observability & telemetry

- [x] Adopt OpenTelemetry GenAI semantic conventions (`gen_ai.system`, `gen_ai.request.model`, `gen_ai.usage.input_tokens`, etc.) in the core — no more custom `llm.*` tags. *(M2b: centralised in `AgenticTags`; see ADR 0002.)*
- [x] Provide `Agentic.Observability.OpenTelemetry` (published as `Vais2.Agents.Observability.OpenTelemetry`) that registers activity sources and meters. *(M2b: `AgenticOpenTelemetryExtensions.AddAgenticInstrumentation()` for both `TracerProviderBuilder` and `MeterProviderBuilder`.)*
- [x] Provide `Agentic.Observability.Langfuse` (`Vais2.Agents.Observability.Langfuse`) as the first first-party enricher (drop-in replacement for today's filter). *(M2b.)*
- [x] Ship a neutral `IUsageSink` + default `OpenTelemetryUsageSink` that writes metrics to OTel counters and histograms. *(M2b: `IUsageSink` landed in M2a; `OpenTelemetryUsageSink` lands here, emitting `gen_ai.client.token.usage` and `gen_ai.client.operation.duration` histograms.)*
- [ ] VAIS2 keeps writing to Postgres via its adapter sink. *(W9 — deferred migration.)*
- [ ] Bridge the current `ActivityListener` bootstrap code in VAIS2's `Program.cs` into an `AddAgenticUsageSink()` extension so no one has to copy-paste 200 lines again. *(W9 — deferred migration.)*

### W7 — Persistence providers

- [x] `Agentic.Persistence.Redis` — Orleans clustering + grain storage shipped as `Vais2.Agents.Persistence.Redis` (M3b). `UseAgenticRedisClustering(ISiloBuilder / IClientBuilder, connectionString)` and `AddAgenticRedisGrainStorage(ISiloBuilder, connectionString)` — thin wrappers over Orleans' built-ins that bake in `AiAgentGrain.StorageName`. Testcontainers-backed integration tests prove grain state rehydrates across `IManagementGrain.ForceActivationCollection` when Redis is the store. **Stream provider deliberately deferred** — no point shipping a Redis-streams helper before the neutral `IAgentEventBus` + `AgentEvent` contracts land (M3e).
- [x] `Agentic.Persistence.Postgres` — Orleans clustering + grain storage shipped as `Vais2.Agents.Persistence.Postgres` (M3c). `UseAgenticPostgresClustering(ISiloBuilder / IClientBuilder, connectionString)` and `AddAgenticPostgresGrainStorage(ISiloBuilder, connectionString)` — thin wrappers over Orleans' built-in ADO.NET providers with `Invariant = "Npgsql"` pre-set and `AiAgentGrain.StorageName` baked in. Testcontainers.PostgreSql-backed integration tests; Orleans' own Postgres SQL scripts (Main + Persistence) are embedded as resources in the test project since they ship only in Orleans' source tree, not in its NuGet packages. **`IChatHistoryStore` deliberately deferred** — only one would-be implementation right now (Postgres) so the interface would be shaped by a single case; the `StatefulAgentOptions.InitialHistory` hook from M3a already covers the "load on construct" half of the need, and a store-as-filter design can land later without breaking current API.
- [ ] `Agentic.Persistence.InMemory` — dev/test. *(Lower priority; `AddMemoryGrainStorage` + the InMemory hosting package already cover the dev/tests story. Stand-alone `Agentic.Persistence.InMemory` package only needed if someone wants the grain-storage bits without the Orleans runtime, which hasn't come up.)*
- [x] `Agentic.Persistence.VectorData` — shipped as `Vais2.Agents.Persistence.VectorData` in M3d. Contract in Abstractions, `VectorStoreKnowledgeRetriever<TKey, TRecord>` over `Microsoft.Extensions.VectorData.Abstractions 9.7.0`, `KnowledgeRetrievalFilter` hooks into the existing M2a `IAgentFilter` pipeline. Test project uses `Microsoft.SemanticKernel.Connectors.InMemory 1.63.0-preview` as the vector store and a deterministic SHA256-based `IEmbeddingGenerator` fake.
- [ ] Document required schemas / keyspaces for each.

### W8 — Cloud runtime

This is the bigger bet: a **managed multi-tenant service** that hosts customer-defined agents behind an API. Tasks:

#### 8.1 Architecture decisions (research)

- [x] **Agent definition format: MAF `DeclarativeAgent` schema** (decision §4.6). MVP ships a documented subset; we converge on the upstream schema as it stabilises. Compiled `.dll` upload is **out of scope for v1**.
- [ ] **Isolation model** — per-tenant Orleans silo? Shared silo with tenant-partitioned grains? Per-tenant k8s namespace? Each option has different pricing floors.
- [ ] **Identity / multi-tenancy** — OIDC (Keycloak / Entra ID / Auth0). API keys for server-to-server. Tenant ID carried in every invocation.
- [x] **Agent invocation API: A2A-native day one** (decision §4.5), with REST and gRPC shapes offered alongside. All three hit the same invocation core (auth → route → invoke → stream). Streaming over A2A uses its native streaming; REST uses SSE; gRPC uses server-streaming.
- [ ] **Metering / billing** — per-token, per-second-of-compute, per-invocation. Needs the usage sink from W6.
- [ ] **Secrets / model keys** — BYO-key (customer brings OpenAI/Anthropic key, stored encrypted) vs. platform-provided (we resell capacity). MVP: BYO-key.
- [x] **RAG / memory**: `Microsoft.Extensions.VectorData` connectors (Azure AI Search, Qdrant, Postgres+pgvector, Redis) via the `Agentic.Persistence.VectorData` package (decision §4.7).

#### 8.2 Build tasks

- [ ] `Agentic.Runtime.Api` — ASP.NET Core service:
  - [ ] `POST /v1/agents` — register / update agent definition.
  - [ ] `POST /v1/agents/{id}/invocations` — synchronous invocation.
  - [ ] `POST /v1/agents/{id}/invocations:stream` — SSE / WebSocket streaming.
  - [ ] `GET /v1/agents/{id}/threads/{threadId}` — state snapshot.
  - [ ] `GET /v1/usage` — billing data.
  - [ ] **A2A protocol endpoints — day-one deliverable** (decision §4.5). Publish an A2A-compliant agent card per registered agent; accept A2A task requests and stream events back. Pin a specific A2A spec revision per runtime release (tracked in an ADR).
  - [ ] OpenAPI spec + generated SDKs.
- [ ] `Agentic.Runtime.Silo` — Orleans silo hosting declarative / compiled agents.
- [ ] `Agentic.Runtime.Cli` — `agentic login`, `agentic deploy agent.yaml`, `agentic invoke`, `agentic logs --follow`.
- [ ] Container images + Helm chart. Reuse `k8s/` patterns from VAIS2 where sensible.
- [ ] Admin UI (later) — tenant dashboard for agent management, traces, usage.

#### 8.3 Security

- [ ] Threat-model the runtime early (STRIDE on agent upload → execution → egress).
- [ ] Sandbox tenant code (if compiled DLLs are ever allowed). Default: no compiled code in v1.
- [ ] Egress controls on tool HTTP calls (URL allowlist, rate limits).
- [ ] Prompt-injection defences — document them, not "solve" them, but have a layered approach (tool-call allowlist, output validation, structured outputs via `OutputSchema` subnode).
- [ ] Secrets at rest: envelope encryption with customer-held KEK (KMS / Key Vault) for enterprise tier.

### W9 — Migration of VAIS2 to the new library **(DEFERRED — done later, after OSS preview)**

> **Scope decision:** this workstream is explicitly **out of scope for the current effort**. Run it only after the OSS library reaches public preview (Phase 3) and its API has stabilised. VAIS2 continues using its internal `Vais2Agents.*` projects in the meantime.

When the OSS library is stable, the migration work is:

- [ ] Replace `ProjectReference`s in VAIS2 with `PackageReference`s to the new NuGet packages.
- [ ] Delete `Backend/Actors/Vais2Agents.*` once consumers are cut over.
- [ ] Keep `Backend/Editor/Agents/*` in VAIS2 (these are product-specific); retarget their base types from `Vais2Agents.Orleans.AiAgent<T>` to `Agentic.Hosting.Orleans.AiAgent<T>`.
- [ ] Write VAIS2-side adapter implementations:
      - [ ] `Vais2AgentContextAccessor : IAgentContextAccessor` — reads `RequestContext.Get("UserId"/"ProjectId"/"FlowId")`.
      - [ ] `Vais2TokenUsageSink : IUsageSink` — bridges to the existing `ITokenUsageTracker` / `TokenUsageRequest`.
      - [ ] `Vais2AgentPluginConfigurator : IKernelConfigurator` — wraps the existing `IAgentPluginManager`.
- [ ] Swap `ThirdParty/SemanticKernelPooling` for keyed `IChatClient` registrations (via `Agentic.Ai.SemanticKernel`), or keep it temporarily behind a compat bridge during cut-over.
- [ ] Verify all flows / copilot scenarios still pass end-to-end; add an integration-test harness in VAIS2 that pins compatible library versions.
- [ ] Document a "VAIS2 uses Agentic" architecture diagram in `PROJECT_SUMMARY.md`.

### W10 — Go-to-OSS readiness

- [ ] **License hygiene**: scan for copied-in code without attribution; confirm DuckDuckGo / any scraper logic is OSS-compatible.
- [ ] **No-secrets audit**: scrub the git history — ensure no API keys / `appsettings.Development.json` / customer data leaked in.
- [ ] **Trademark / naming check**: "Agentic" is a crowded term; run a trademark / NuGet-existing-package check before committing.
- [ ] **Public API review checklist**: run framework-design-guidelines pass; minimise the v1 surface (add later is easy, remove is breaking).
- [ ] **Samples**: minimum 3 — "hello agent", "multi-agent debate", "agent with tools + memory", each in SK and MAF variants.
- [ ] **Benchmarks**: a small `BenchmarkDotNet` suite for grain activation latency, completion throughput per provider.
- [ ] **Supply chain**: enable Dependabot / Renovate; sign NuGet packages; enable GitHub security advisories.
- [ ] **Launch plan**: blog post, HN/Reddit, conference submission (.NET Conf, NDC).

---

## 4. Resolved decisions

The 8 open questions have been answered. These are now **binding decisions** that shape the rest of this document and the follow-up `/specify` work.

| # | Decision | Rationale / implications |
|---|----------|---------------------------|
| 1 | **New standalone repo** for the OSS project (e.g. `github.com/<org>/agentic`) | Clean OSS hygiene, separate issue tracker / releases / contributors. Requires a pre-release NuGet feed (GitHub Packages or Azure Artifacts) that VAIS2 consumes during development. |
| 2 | **Orleans-first, with a small `InMemory` host** for dev/tests/samples | Production value is Orleans (durability, clustering, streams). An in-memory host keeps the hello-world path friction-free. Target ~1.2× maintenance, not 2×. `IAgentRuntime` must be carefully shaped so both hosts satisfy it without compromise. |
| 3 | **Apache 2.0 for the entire stack** (library *and* cloud runtime) | No license-based moat — competitive advantage comes from hosted ops quality, reliability, and brand. Explicitly avoids BSL/SSPL friction. The Helm chart is first-class: anyone can self-host. Commercialisation path is a managed offering, not a license fence. |
| 4 | **Microsoft Agent Framework is the primary/default** in docs and samples; SK is fully supported as the legacy/alternative stack | Rides Microsoft's strategic direction. All docs lead with MAF; SK samples shown as "also supported". Risk: MAF APIs still stabilise through 2026 — we pin versions aggressively and carry parity tests. |
| 5 | **Adopt the A2A (Agent-to-Agent) protocol on day one** of the cloud runtime | Ecosystem interop for free: MAF clients, LangGraph, and other A2A-compatible systems can call hosted agents without our SDK. Cost: upfront spec-compliance work + schema-versioning discipline. REST/gRPC remain available alongside A2A, not instead of it. |
| 6 | **Adopt MAF's `DeclarativeAgent` schema** for the cloud runtime's agent definition format, once stable; interim schema is a subset we commit to aligning with | Avoids reinventing a schema. Portable definitions: users can take the same YAML to any MAF-compatible host. Interim risk: MAF schema is still evolving — our MVP uses a carefully-chosen subset and we absorb the final schema when it ships. |
| 7 | **Port `AddKnowledge` to `Microsoft.Extensions.VectorData`** | Supported path, works for both SK *and* MAF, and has first-party connectors (Azure AI Search, Qdrant, pgvector, Redis). Dropping `ISemanticTextMemory` avoids tying the library to a deprecated SK surface. |
| 8 | **Replace `SemanticKernelPooling` with keyed `IChatClient` + provider retry policies** (Polly / provider SDK retries) | MAF-aligned path via `Microsoft.Extensions.AI`. Removes a bespoke component with a maintenance tail. Multi-key/multi-provider load balancing implemented as keyed DI registrations; the VAIS2 SK adapter gets a feature-parity wrapper during migration. |

### 4.1 How these decisions reshape the plan

- **MAF is first-class, not bolted on.** W5 (MAF adapter) now runs **in parallel with** — not after — the SK adapter in W4. The abstraction layer in W3 is validated by both stacks landing together.
- **`Microsoft.Extensions.AI.IChatClient` is the unifying completion abstraction.** Both SK and MAF adapters speak `IChatClient` internally. `ICompletionProvider` in W3 becomes a thin wrapper around `IChatClient` rather than a parallel abstraction.
- **No `SemanticKernelPooling` in the published surface.** Its multi-key / multi-provider semantics are re-implemented as keyed `IChatClient` registrations + a `LoadBalancingChatClient` decorator in `Agentic.Ai.SemanticKernel`.
- **`Agentic.Persistence.VectorData` is a new package** (formerly "port `AddKnowledge`"). It wraps `Microsoft.Extensions.VectorData` behind an `IKnowledgeRetriever` abstraction.
- **Cloud runtime API is A2A-native**, with REST/gRPC as supplementary shapes over the same invocation core. The invocation pipeline (auth → route to tenant silo → invoke agent → stream) is shared.
- **Self-hostable Helm chart is a day-one deliverable**, not a follow-on. Apache-2.0 across the stack means the published chart must be feature-complete for self-hosters; the hosted offering differs only in ops (upgrades, SLA, support), not features.

### 4.1a Scope change — VAIS2 is not touched in this effort

**W1 (VAIS2 decoupling) and W9 (VAIS2 migration) are deferred.** The OSS library is built greenfield from a *copy* of `Vais2Agents.*` inside the new repo and decoupled there. VAIS2 keeps its internal `Vais2Agents.*` projects running unchanged. The OSS library must therefore validate itself against its own tests + samples, not against VAIS2.

Consequences:
- W3 absorbs all the decoupling work that W1 used to do — done against neutral stubs / fakes in the OSS repo's test suite.
- No VAIS2 code changes, no `[Obsolete]` shims, no pre-release feed for VAIS2 consumption — **not yet**.
- Phase 1 is skipped entirely; the sequence goes straight from Phase 0 (research) to Phase 2 (extraction + both adapters), renumbered below.
- The VAIS2 adapter implementations (`Vais2AgentContextAccessor`, `Vais2TokenUsageSink`, etc.) are *noted* in W9 for later but not written now.

### 4.2 Remaining non-blocking questions (to answer during Phase 0)

These were subsumed by the decisions above but still need concrete values before the extraction phase starts:

- [ ] **Org / repo name** — need a final name for the GitHub org and repo ("Agentic" is crowded on NuGet; may need a prefix like `<org>.Agentic.*`). Trademark search due in W10.
- [ ] ~~**Pre-release feed**~~ — **no longer relevant now**, since VAIS2 is not consuming the OSS packages in this phase. Revisit when W9 is picked up.
- [ ] **Which MAF `DeclarativeAgent` subset** is safe to commit to today? Needs a small prototype against current MAF bits.
- [ ] **A2A spec version pin** — which A2A schema revision is v1 of our runtime conformant to? Tracked in an ADR.
- [ ] **Keyed-`IChatClient` DI convention** — string keys (`"openai:gpt-4o:primary"`) vs structured keys via `ServiceKey` record. Affects every consumer.

---

## 5. Recommended delivery phases (updated — Phase 1 dropped)

Two shapes changed: **(a)** MAF ships alongside SK, not after; **(b)** VAIS2 is not modified in this effort, so the old Phase 1 (internal refactor) is removed and all decoupling happens in the OSS repo. Phases are renumbered.

**Phase 0 — Research & prototyping (1–2 weeks)** — ✅ **DONE**
- Answer the remaining non-blocking questions in §4.2 (repo/org name, MAF schema subset, A2A spec pin, keyed-DI convention).
- Prototype a MAF-backed agent *and* an SK-backed agent sitting on the same draft `ICompletionProvider` / `IChatClient`, in a scratch repo — proves the abstraction before code is committed to the OSS repo.
- Trademark / NuGet-name clearance for the final package prefix.
- **No code is written in VAIS2.**
- **Outcome:** spike at `spike/agentic-phase0/` proved abstraction viable end-to-end against OpenAI on both stacks. See §8 milestone log for details.

**Phase 1 — Library extraction + both adapters (4–6 weeks)** *(was Phase 2)* — ✅ **FEATURE-COMPLETE (tag `v0.1.0-preview`)**
- Stand up the new standalone OSS repo (W2) under Apache 2.0. ✅ `oss/agentic/` created, Apache-2.0, own git repo, CI wired.
- Copy `Vais2Agents.*` in and decouple in place — all of W3, which absorbs the decoupling work formerly in W1. ✅ Core decoupling done through M2c; `IKernelConfigurator` / `IAgentConfigurator` / `IAgentEventBus` / `IAgentStateStore` deferred to M3 (see §7).
- Ship **SK adapter (W4) and MAF adapter (W5) in parallel** — the release bar is parity tests green on both, against the OSS repo's own test suite. ✅ Completion + tool-calling parity landed through M2c; 5/5 parity tests green. Streaming parity, multi-agent orchestrator still deferred.
- Ship observability (W6), persistence incl. `Vais2.Agents.Persistence.VectorData` (W7). ✅ Observability (W6) done in M2b. ⏳ Persistence/RAG in M3.
- VAIS2 is not touched.
- **Milestone breakdown for this phase:**
  - **M1 (done):** Repo scaffold + Abstractions + Core + SK + MAF minimal adapters + HelloAgent sample + Core unit tests + CI. *Tag `v0.0.1-alpha`.*
  - **M2a (done):** Remaining-but-not-all neutral contracts (`IUsageSink`, `IAgentContextAccessor`, `IAgentFilter`, `IAgentRuntime`), expanded `StatefulAiAgent` with resilience/filters/usage, `Vais2.Agents.Hosting.InMemory`, PublicAPI analyzer enforcing, ADR 0001 for keyed-DI convention. *Tag `v0.0.2-alpha`.*
  - **M2b (done):** `Vais2.Agents.Observability.OpenTelemetry` — OTel GenAI semantic conventions, default `OpenTelemetryUsageSink`, plus `Vais2.Agents.Observability.Langfuse` enricher. `StatefulAiAgent` emits per-turn Activity with `gen_ai.*` tags. ADR 0002 pins the naming. *Tag `v0.0.3-alpha`.*
  - **M2c (done):** Tool-calling parity — `ITool` + `IToolRegistry` in Abstractions, `CompletionRequest.Tools`, `SkToolBinder` and `MafToolBinder` converging through MEAI `AIFunction`, `StatefulAgentOptions.ToolRegistry` plumbing, formal `Vais2.Agents.ParityTests` project (5 tests). `IKernelConfigurator` / `IAgentConfigurator` deferred to M3. *Tag `v0.0.4-alpha`.*
  - **API-freeze sweep (done, 2026-04-18):** Promoted ≈243 Phase-1 entries `PublicAPI.Unshipped.txt` → `PublicAPI.Shipped.txt` across the seven packages; dog-fooded the surface by extending `HelloAgent` with a tool-calling segment; produced seven `0.1.0-preview` `.nupkg` + `.snupkg` into `oss/agentic/artifacts/packages/` (local-only, not pushed); consumer-smoke-tested from a throwaway .NET 9 console app. Commit `a629213`, annotated tag `v0.1.0-preview`. Two dog-food findings (`ChatRole` collides with MEAI, `SkCompletionProvider` ctor fail-fast) deferred to 0.2. 38/38 tests green.
  - **M3a (done, 2026-04-18):** `Vais2.Agents.Hosting.Orleans` — `IAiAgentGrain` + sealed `AiAgentGrain` wrapping the neutral `StatefulAiAgent` with `IPersistentState`-backed history retention; `OrleansAgentRuntime` + client proxy bridging the grain into `IAiAgent`; `OrleansAgentContextAccessor` reading `RequestContext`; serialisation surrogates keep Abstractions Orleans-free. 12 TestingHost tests green. Tag `v0.2.0-alpha` (pending).
  - **M3b (done, 2026-04-18):** `Vais2.Agents.Persistence.Redis` — `UseAgenticRedisClustering` + `AddAgenticRedisGrainStorage` wrappers over Orleans' built-in Redis providers, baking in the `AiAgentGrain.StorageName` convention. 5 Testcontainers-backed integration tests prove grain state round-trips through real Redis and rehydrates across `ForceActivationCollection`. **Redis streams intentionally excluded** — deferred to M3e alongside the neutral `IAgentEventBus` / `AgentEvent` contracts. Tag `v0.3.0-alpha` (pending).
  - **M3c (done, 2026-04-18):** `Vais2.Agents.Persistence.Postgres` — `UseAgenticPostgresClustering` + `AddAgenticPostgresGrainStorage` wrappers over Orleans' ADO.NET providers with `Invariant = "Npgsql"` hardcoded. 5 Testcontainers-backed integration tests; Orleans' Postgres SQL scripts embedded as test resources (Orleans' NuGet doesn't ship them). `IChatHistoryStore` deferred until a second implementation can shape the interface. Tag `v0.4.0-alpha`.
  - **M3d (done, 2026-04-18):** `Vais2.Agents.Persistence.VectorData` — neutral `IKnowledgeRetriever` + `KnowledgeChunk` in Abstractions (unshipped addition), `VectorStoreKnowledgeRetriever<TKey, TRecord>` over `Microsoft.Extensions.VectorData.Abstractions 9.7.0`, `KnowledgeRetrievalFilter : IAgentFilter` augmenting the request's `SystemPrompt` with retrieved chunks (retrieved context never enters `IAiAgent.History`). Works for both adapters because integration is above the completion provider. 10 tests (4 retriever + 6 filter) using `Microsoft.SemanticKernel.Connectors.InMemory 1.63.0-preview`. Tag `v0.5.0-alpha` (pending).
  - **M3e-1 (done, 2026-04-18):** cross-host parity harness — `Vais2.Agents.CrossHostTests` exercises the same deterministic scenario across `InMemoryAgentRuntime`, Orleans+Redis, and Orleans+Postgres, snapshots history / filter invocations / usage records per host, and asserts byte-for-byte equality. Also a rehydration test. 2 tests. *Tag `v0.6.0-alpha` (pending).*
  - **M3e-2 (done, 2026-04-18):** streaming parity — new Abstractions types `CompletionUpdate` + `IStreamingCompletionProvider`; `StatefulAiAgent.StreamAsync` yields `IAsyncEnumerable<string>` text deltas with per-turn Activity + usage reporting (filters + resilience intentionally bypassed for v1). SK adapter maps `IChatCompletionService.GetStreamingChatMessageContentsAsync` → `CompletionUpdate`. MAF adapter maps `AIAgent.RunStreamingAsync` → `CompletionUpdate`. Core 6 + Parity 3 new tests. *Tag `v0.7.0-alpha` (pending).*
  - **M3e-3a (done, 2026-04-18):** neutral agent-event bus — Abstractions adds `abstract record AgentEvent(At, Context)` + `TurnStarted` / `TurnCompleted` / `TurnFailed` + `IAgentEventBus`. `Vais2.Agents.Hosting.InMemory` ships `InMemoryAgentEventBus` (ImmutableArray fan-out). `StatefulAiAgent` publishes events from `AskAsync` and `StreamAsync`; bus failures swallowed like usage sink. 10 new tests. *Tag `v0.8.0-alpha` (pending).*
  - **M3e-3b (done, 2026-04-18, REFRAMED):** Orleans-streams-backed event bus (provider-neutral) — discovered `Microsoft.Orleans.Streaming.Redis` has no stable 9.x release (only `10.1.0-alpha.1`, requires Orleans 10.x). Reframed to provider-neutral `OrleansAgentEventBus` + Orleans surrogates + base `Microsoft.Orleans.Streaming` dependency. Memory streams in tests prove the full wire. Consumers pick the stream provider (memory for dev, AzureEventHubs for cloud, etc.). Redis-streams-specific extension waits for a stable 9.x package. *Tag `v0.9.0-alpha` (pending).*
  - **M3e-4 (done, 2026-04-18):** multi-agent orchestrator neutral contract — `IAgentOrchestrator` + `AgentParticipant` + `OrchestrationStep` in Abstractions; `SequentialOrchestrator` (pipeline) + `RoundRobinOrchestrator` (shared-history group chat with `TerminationPredicate`) in Core. Participants drive `ICompletionProvider` directly to avoid per-agent-history interference. SK-/MAF-framework-specific orchestrator wrappers deferred. 8 new tests. *Tag `v0.10.0-alpha` (pending). **Closes M3e and Phase 1 Milestone 3 entirely.***

**Phase 2 — First public preview (1 week)** *(was Phase 3)* — 🟡 **PARTIAL (local `v0.4.0-preview` cut; public push still pending)**
- Cut preview NuGet packages. ✅ Four local cuts ship one after another on OSS repo `main`, none pushed: `0.1.0-preview` (7 packages, Phase 1 Milestones 1-2c) → `0.2.0-preview` (11 packages, closes M3 + all Phase 1) → `0.3.0-preview` (11 packages, 10.x dep upgrade + Orleans Redis streams) → **`0.4.0-preview` (13 packages, all ten architectural-review pillars)**. 13 `.nupkg` + 13 `.snupkg` sit in `oss/agentic/artifacts/packages/`. Smoketest rewritten at every cut — 0.4 version exercises every pillar surface (session, memory, context, prompt, guardrails, RunBudget, dispatcher, toolsource, termination/handoff, AgentManifest + registry, MCP + A2A) against the packaged feed.
- Publish docs site, announce on one small channel (not HN yet). ⏳ Pending — abstractions are now design-partner-ready but the MAF/MCP/A2A preview pins are still tracking preview SDKs.
- Collect feedback for 2–3 weeks before public NuGet push. ⏳ Pending.

**Phase 3 — Cloud runtime MVP (6–8 weeks)** *(was Phase 4)*
- W8: A2A-native runtime + REST/gRPC, declarative-agent-only v1, BYO-key, Apache-2.0 Helm chart, hosted reference deployment.
- An A2A interop smoke test with at least one external client (LangGraph / MAF / A2A reference implementation) gates the release.

**Phase 4 — Public launch (ongoing)** *(was Phase 5)*
- W10: blog post, HN/Reddit, conference submissions, samples tour.
- Hosted-runtime onboarding for early design partners.

**Phase 5 — VAIS2 migration to the OSS library (separate effort, deferred)**
- W1 + W9 together: decouple VAIS2's internal consumers and swap them to the published NuGet packages. Run only once the OSS API is stable (post-Phase 2 preview, ideally post-`1.0`).

---

## 6. Risks

- **MAF is young.** Breaking changes between minor versions are likely through 2026; we must version-pin aggressively and design the adapter to be replaceable.
- **Two adapters doubles maintenance.** Parity tests are non-optional.
- **Orleans licensing / complexity perception.** Orleans is MIT and mature but carries a learning-curve tax for users who only wanted a simple agent library. Mitigation: ship the `InMemory` host for simple cases, make Orleans opt-in.
- **Multi-tenancy in the cloud runtime** is the hardest problem — easy to under-estimate (isolation, egress control, prompt-injection blast radius). Start conservative (declarative agents only).
- **Trademark / naming** disputes with existing "Agentic*" packages could force a rename late.
- **VAIS2 velocity hit** during Phase 1 refactor. Keep the decoupling changes behind `[Obsolete]` shims so concrete agents migrate one at a time, not in a big-bang.

---

## 7. Next action

**v0.4.0-preview is cut locally — all ten architectural-review pillars (§§9.1-9.10) are closed.** OSS repo `main` carries (chronologically): `v0.1.0-preview` → M3 alpha tags → `v0.2.0-preview` (closes Phase 1) → `v0.3.0-preview` (10.x dep upgrade) → **`v0.4.0-preview` (9c73a4b, 2026-04-18)**. The v0.4 cut includes 15 pillar PRs (session → memory → context → prompt → guardrails → execution loop → tools → orchestration → control plane → MCP/A2A interop) + 1 API-freeze commit; 287/287 non-container tests green at freeze; 13 `.nupkg` + 13 `.snupkg` at `0.4.0-preview` sit in `oss/agentic/artifacts/packages/`; smoketest exercises every pillar against the packaged feed cleanly. **Post-freeze (2026-04-19)** landed three additive follow-ups on top of the tagged commit: (1) MCP `0.1.0-preview.10` → `ModelContextProtocol.Core 1.2.0` (commit `cf6c883`); (2) A2A `0.3.1-preview` → `A2A 1.0.0-preview2` (protobuf-style SDK reshape: `Message`/`Part`/`Role` renames, `PartContentCase` discriminator, per-method `*Request` records, 12-method `IA2AClient`, streaming absorbed SSE); (3) tool-using streaming — `StatefulAiAgent.StreamAsync` now wraps the outer tool-call loop mirroring `AskAsync`, `CompletionUpdate` gains a nullable `ToolCalls` field, SK + MAF adapters accumulate tool calls across streamed updates and emit a terminal update carrying them. All four post-freeze packages (Abstractions + Core + both adapters + two interop packages) repacked at `0.4.0-preview`; 280/280 non-container tests green; smoketest re-runs clean; tag still on `9c73a4b`.

**Update 2026-04-19 — `v0.5.0-preview` cut locally.** Durable-execution pillar closed in 5 PRs (`0cb5d81` → `c50ac46`) + annotated tag `v0.5.0-preview`. Surface: `IAgentJournal` + `JournalEntry`/`ToolCallRecorded` (tool-call-only granularity by MVP design) + `NullAgentJournal`/`InMemoryAgentJournal` + `IAgentRunJournalGrain`/`AgentRunJournalGrain` + `OrleansAgentJournal` + `AgentContext.RunId` + `StatefulAgentOptions.Journal`/`RunIdFactory` + `ToolCallReplayed` event + `AgentInterrupt.RunId` / `ResumeInput.RunId`. `StatefulAiAgent.ResumeAsync` is now a real operation — threads the caller-supplied `RunId` so the dispatcher cache-replays journaled tool outcomes instead of re-invoking the tool after an HITL pause or crash. 13 packages repacked at `0.5.0-preview`; 345/345 non-container tests green; smoketest re-runs clean with a new durable-execution segment. Full details: [`actor-agents-oss-v0.5-durable-execution-pillar.md`](./actor-agents-oss-v0.5-durable-execution-pillar.md).

**Update 2026-04-19 — `v0.6.0-preview` cut locally.** Control-plane pillar closed in 5 PRs (`2774933` → `816e2b9`) + annotated tag `v0.6.0-preview`. Six new packages (`Vais.Agents.Control.Abstractions` + `.InProcess` + `.Manifests.Json` + `.Manifests.Yaml` + `.Http.Server` + `.Http.Client`) plus an expanded `AgentManifest` on the shipped Abstractions side (model / systemPrompt / mcpServers / guardrails / handoffs / budget / contextProviders / outputSchema / agentMode / reasoning / observability / annotations — all init-only, zero *REMOVED* churn). Engine: `AgentLifecycleManager` routes the seven universal verbs through `IAgentPolicyEngine` + `IAuditLog` middleware; runtime-neutral so Orleans consumers wire `OrleansAgentRuntime` as their `IAgentRuntime` and use the same manager. Wire: YAML + JSON manifest loaders sharing one validation core (preserves key order end-to-end for SGR reasoning schemas); HTTP minimal-API surface mapping all verbs to REST under `/v1`; RFC 7807 Problem Details with stable `urn:vais-agents:*` type URNs; typed `AgentControlPlaneClient` wrapping `HttpClient`. Auth: JWT bearer via `AddAgentControlPlaneJwtAuth()`, default OIDC `IPrincipalMapper`, per-request middleware that pushes `AgentPrincipal` onto the ambient context. Observability: `LoggerAuditLog` default + `Vais.Agents.Control` `ActivitySource` + `Meter` (`vais.control.verb.{duration,count}`) on the hot-path Create/Invoke verbs. Secret resolvers: env + file + composite dispatch. Reasoning-layer fields + non-default `agentMode` are contract-only — the engine treats everything as `toolCalling`; SGR execution lands in a follow-up pillar. 19 packages repacked at `0.6.0-preview`; 422/422 non-container tests green; smoketest re-runs clean with a new control-plane segment. Full details: [`actor-agents-oss-v0.6-control-plane-pillar.md`](./actor-agents-oss-v0.6-control-plane-pillar.md) + [`actor-agents-oss-v0.6-manifest-schema.md`](./actor-agents-oss-v0.6-manifest-schema.md).

**Nothing pushed publicly.** Next decision: **design-partner feedback round vs. public NuGet push**. The abstraction surface is now wide (roughly 28 new contracts across 6 harness pillars + control plane + interop + durable execution + control-plane wire), so a small design-partner round before public push is a cheap way to catch awkward shapes. Post-v0.6 backlog (deferred explicitly; none blocks a push):

- ~~Kubernetes CRDs + operator (`Vais.Agents.Control.KubernetesOperator`) — declarative agents as native K8s resources; reconciler drives `IAgentLifecycleManager` verbs to match cluster state.~~ **Landed 2026-04-20** as the v0.13 pillar (`v0.13.0-preview`). New NuGet library package `Vais.Agents.Control.KubernetesOperator` (package count 22 → 23) with `AgentEntity : CustomKubernetesEntity<AgentSpec, AgentStatus>` (`vais.io/v1alpha1`, namespaced, short names `vagent`/`vagents`) + `AgentEntityController : IEntityController<AgentEntity>` (6-row reconcile decision table + `Idempotency-Key={uid}:{generation}:{verb}` on every call) + `AgentEntityFinalizer` (KubeOps-managed finalizer `vais.io/agent-deactivate` → `EvictAsync` unless `preserveOnDelete=true`) + `ServiceAccountTokenHandler` (projected-volume bearer token + TTL+mtime cache) + `ServiceAccountPrincipalMapper` (optional). In-repo-only `Vais.Agents.Control.KubernetesOperator.Host` exe + `Dockerfile` (alpine, non-root uid 65532) + Helm chart at `deploy/helm/vais-agents-operator/` + hand-rolled CRD at `deploy/crds/vais.io_agents.yaml`. Single CRD (`Agent` only) — `AgentGraph` → v0.14 paired with `IAgentGraphRegistry`; `AgentRun` → v0.15 paired with `IAgentRunRegistry` + `GET /v1/agents/{id}/runs/{runId}`. KubeOps 10.3.4 backbone; `x-kubernetes-preserve-unknown-fields: true` on `.spec` + `.status` due to KubeOps transpiler's TimeSpan intolerance. `secretRefs` on CR is validation-only in v0.13 (runtime-side inline-secret wire format deferred). 42 new tests / 611/611 total. See [`actor-agents-oss-v0.13-kubernetes-operator-pillar.md`](./actor-agents-oss-v0.13-kubernetes-operator-pillar.md) + [`actor-agents-oss-v0.13-kubernetes-operator-findings.md`](./actor-agents-oss-v0.13-kubernetes-operator-findings.md).
- ~~Real policy engine (`Vais.Agents.Control.Policy.Opa`) — OPA/Rego adapter behind the `IAgentPolicyEngine` contract shipped in v0.6.~~ **Landed 2026-04-20** as the v0.14 pillar (`v0.14.0-preview`). New NuGet library `Vais.Agents.Control.Policy.Opa` (package count 23 → 24). `OpaPolicyEngine : IAgentPolicyEngine` backed by sidecar HTTP (`POST /v1/data/{DataPath}`) with a 6-step evaluate state machine + `DecisionCache` (SHA-256-keyed, 5s TTL, 1024-entry bound, 25% oldest-by-timestamp purge) + lazy `GET /v1/status` policy-version log + `FailMode=Closed` enterprise-safe default + 4xx→throw / 5xx→FailMode separation. Input schema v1 locked at `{schemaVersion, operation, principal, agent}` with the full `AgentManifest` via STJ camelCase; response parser accepts both `bool` and `{allowed, reason}` shapes. `AddOpaPolicyEngine` DI extension wires typed HttpClient + singleton `IAgentPolicyEngine` seam. Samples at `samples/opa-policies/{tenant-scoped-allow, model-provider-allowlist, budget-cap}.rego` + sidecar overlay doc at `samples/opa-sidecar/` + full schema at `contracts/opa-input-schema.md`. 33 unit tests + 6 Testcontainers-backed integration tests against `openpolicyagent/opa:1.15.2`; full non-container suite **644/644**. See [`actor-agents-oss-v0.14-opa-policy-engine-pillar.md`](./actor-agents-oss-v0.14-opa-policy-engine-pillar.md) + [`actor-agents-oss-v0.14-opa-policy-engine-findings.md`](./actor-agents-oss-v0.14-opa-policy-engine-findings.md).
- ~~CLI (`vais apply / get / invoke / logs / signal`) over the HTTP client.~~ **Landed 2026-04-20** as the v0.15 pillar (`v0.15.0-preview`). New NuGet package `Vais.Agents.Cli` (package count 24 → 25) as a **dotnet tool** (`<PackAsTool>true</PackAsTool>` + `<ToolCommandName>vais</ToolCommandName>`). Install via `dotnet tool install -g Vais.Agents.Cli --version 0.15.0-preview`. 14 subcommands mapping 1:1 to `IAgentControlPlaneClient`: `apply -f` (create-or-update via 409 fallback) + `get agents [-o table/yaml/json]` + `invoke` (unary + `--stream`) + `logs` (live-run SSE attach with `--only` / `--since` filters) + `signal` (inline JSON or `@file.json`) + `delete` (TTY-aware confirm) + `cancel` + `init <name>` scaffold + `version` + `config {get-contexts/current-context/use-context/set-context}`. Config = `~/.vais/config.yaml` kubectl-shape (`clusters + users + contexts + currentContext`) with `VAIS_CONFIG` override. Auth precedence `--token` > `VAIS_TOKEN` env > context user's `token`/`tokenFile`. Exit codes POSIX `0/1/2/3/4/130` with Problem-Details-aware error routing (401→4, 403+policy-denied URN→3, 5xx/other→2, SIGINT→130). Framework = `Spectre.Console.Cli 0.55.0`. 43 unit tests; full non-container suite **687/687**. See [`actor-agents-oss-v0.15-cli-pillar.md`](./actor-agents-oss-v0.15-cli-pillar.md) + [`actor-agents-oss-v0.15-cli-findings.md`](./actor-agents-oss-v0.15-cli-findings.md).
- ~~SSE streaming Invoke on the HTTP surface (wire format + event taxonomy already specified in the v0.6 HTTP-API design doc; server/client impl deferred).~~ **Landed 2026-04-20** as the v0.12 pillar (`v0.12.0-preview`). New `CompletionDelta : AgentEvent` record + `IStreamingAiAgent` capability interface in Abstractions; `StatefulAiAgent` implements via a new `StreamEventsCoreAsync` helper; existing `StreamAsync(string) : IAsyncEnumerable<string>` preserved as a text-projection wrapper. Server-side `POST /v1/agents/{id}/invoke/stream` emits full `AgentEvent` taxonomy as SSE (10 event kinds; `event:` field is the wire discriminator); channel-based multiplex + 15s heartbeat default + `StreamingEndpointAttribute` marker so idempotency middleware skips body-buffering. Client gains 2 DIM overloads (`InvokeStreamAsync` text-only + `InvokeStreamEventsAsync` full events) via `System.Net.ServerSentEvents` built-in parser. Zero new packages. Orleans streaming passthrough deferred (non-streaming-capable agents return 501 `urn:vais-agents:streaming-not-supported`). See [`actor-agents-oss-v0.12-sse-streaming-invoke-pillar.md`](./actor-agents-oss-v0.12-sse-streaming-invoke-pillar.md) + [`actor-agents-oss-v0.12-sse-streaming-invoke-findings.md`](./actor-agents-oss-v0.12-sse-streaming-invoke-findings.md).
- ~~OpenAPI auto-generation + `Idempotency-Key` dedupe store on the HTTP surface.~~ **Landed 2026-04-20** as the v0.11 pillar (`v0.11.0-preview`). New `IIdempotencyStore` in `Control.Abstractions`; `InMemoryIdempotencyStore` + `AgentControlPlaneIdempotencyMiddleware` + `AgentControlPlaneClientOptions` + OpenAPI spec at `GET /openapi/v1.json` via built-in `Microsoft.AspNetCore.OpenApi 9.0.11`; `OrleansIdempotencyStore` for durable dedupe. Stripe-shape semantics — 24h TTL, raw-body SHA-256 fingerprint, 4-tuple tenant-scoped keys, 422 mismatch, 409+Retry-After on in-flight. `VaisProblemDetailsOperationTransformer` attaches `x-vais-type-urns` extension to error responses. Zero new packages. See [`actor-agents-oss-v0.11-openapi-idempotency-pillar.md`](./actor-agents-oss-v0.11-openapi-idempotency-pillar.md) + [`actor-agents-oss-v0.11-openapi-idempotency-findings.md`](./actor-agents-oss-v0.11-openapi-idempotency-findings.md).
- Multi-region / cross-cluster routing + SPIFFE outbound identity + OAuth2 client-credentials resolver.
- ~~Graph orchestration implementation (`IAgentGraphExecutor` / `IAgentGraphBuilder` interfaces were deliberately deferred from §9.7 so the eventual `GraphOrchestrator` gets to shape them).~~ **Landed 2026-04-20** as the v0.9 pillar (`v0.9.0-preview`). Shipped `IAgentGraph<TState>` (+ `IAgentGraph` bag specialisation), `InProcessGraphOrchestrator` in Core (zero-MAF-dep Pregel/BSP), a MAF Workflows adapter package (`Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework`), `kind: AgentGraph` YAML/JSON loader in the existing manifest packages, `OrleansCheckpointer` for durable interrupt→resume across silo restart. K8s-style `{property, operator, value}` edge predicates + boolean combinators; 10 operators; `HandlerRef` escape hatch. See [`actor-agents-oss-v0.9-graph-orchestration-pillar.md`](./actor-agents-oss-v0.9-graph-orchestration-pillar.md) + [`actor-agents-oss-v0.9-graph-orchestration-findings.md`](./actor-agents-oss-v0.9-graph-orchestration-findings.md).
- ~~MCP inbound (`McpAgentServer`) — needs the "agent as MCP" semantic to settle.~~ **Landed 2026-04-19** as the v0.7 pillar (`v0.7.0-preview`); new package `Vais.Agents.Protocols.Mcp.Server` hosts agents over stdio (Claude Desktop spawn) and streamableHttp (web + ContextForge-gateway composition). Semantic locked as "one MCP tool per registered agent id"; manifests published as `agent://{id}/{version}/manifest` resources using the v0.6 control-plane envelope JSON. See [`actor-agents-oss-v0.7-mcp-inbound-pillar.md`](./actor-agents-oss-v0.7-mcp-inbound-pillar.md).
- ~~A2A inbound (`A2AAgentEndpoint` + `OrleansTaskStore : ITaskStore`) — needs the `A2A.AspNetCore` integration choices.~~ **Landed 2026-04-19** as the v0.8 pillar (`v0.8.0-preview`); new package `Vais.Agents.Protocols.A2A.Server` hosts agents as A2A endpoints under `/agents/{id}` with auto-derived `AgentCard`s at `.well-known/agent-card.json`. `Vais.Agents.Hosting.Orleans` extended with `OrleansTaskStore : A2A.ITaskStore` so `input-required` tasks survive silo restart. Unary `message/send` only (SSE deferred); interrupts → `Task(input-required)` with resume-via-`taskId`; JWT auth under scheme `A2AJwt` with the same dual-header pattern as v0.7 MCP. See [`actor-agents-oss-v0.8-a2a-inbound-pillar.md`](./actor-agents-oss-v0.8-a2a-inbound-pillar.md).
- ~~Streaming-filter pipeline + resilience-pipeline wrapping on streamed turns (the synchronous `IAgentFilter` chain + Polly pipeline are still bypassed on `StreamAsync`, same as v0.4; consumers needing filters stay on `AskAsync`). Needs a streaming-filter surface design.~~ **Landed 2026-04-20** as the v0.10 pillar (`v0.10.0-preview`). Widened shipped `IStreamingAgentFilter` with an additive DIM `InvokeAsync(request, next, ct) : IAsyncEnumerable<CompletionUpdate>` around-provider hook (single type, three override points). `StatefulAgentOptions.StreamingResiliencePipeline` as sibling knob to `ResiliencePipeline`; `StatefulAiAgent.StreamAsync` per-turn loop refactored into Phase 1 retry boundary (pre-first-delta-only; per-turn inside the tool-call loop) + Phase 2 drain. Zero adapter code changes — both SK + MAF satisfy the new `IStreamingCompletionProvider` idempotence contract clause by construction. See [`actor-agents-oss-v0.10-streaming-pipeline-pillar.md`](./actor-agents-oss-v0.10-streaming-pipeline-pillar.md) + [`actor-agents-oss-v0.10-streaming-pipeline-findings.md`](./actor-agents-oss-v0.10-streaming-pipeline-findings.md).
- **Temporal parity (roadmap)**: v0.5 delivers the minimum viable durable-execution story — tool-call journaling + cache-replay on resume. The **longer-term aim is parity with Temporal** on the verbs and guarantees that matter for agent runtimes: deterministic replay of the full turn loop (not just tool calls), wall-clock-free timers (`Sleep` / `ContinueAsNew`), child workflows (nested agent runs as first-class journal citizens), versioning (workflow-code changes without breaking in-flight runs), and history compaction. `IAgentJournal` is deliberately shaped so richer entry types (e.g. `AssistantTurnRecorded`, `TimerFired`, `ChildRunInvoked`) can land additively; `IToolCallDispatcher` stays the step boundary. Exact verb mapping (Temporal's `Activity` vs. ours `ITool`, `Signal` vs. `AgentSignal`, `Query` vs. control-plane `Query` verb) is a design task the next time this pillar is opened. Goal is not to be Temporal-in-.NET — it's to make a Temporal migration a boring port, not an architectural rewrite.

- [x] ~~Close the §4.2 non-blocking questions~~ — done (A2A via MAF.MapA2A, keyed-DI convention now ADR 0001, MAF subset pinned at preview, pre-release NuGet feed not relevant until M3).
- [x] ~~Land a MAF+SK prototype~~ — done (spike + productionised in `oss/agentic/`).
- [x] ~~M1: library scaffold + adapters + tests + CI.~~
- [x] ~~M2a: remaining neutral contracts (subset), expanded Core, InMemory host, PublicAPI baselines, DI ADR.~~
- [x] ~~M2b: observability — OTel GenAI conventions, `OpenTelemetryUsageSink`, Langfuse enricher, per-turn activity emission, ADR 0002.~~
- [x] ~~M2c: tool-calling — `ITool` + `IToolRegistry`, SK + MAF binders through MEAI `AIFunction`, `StatefulAgentOptions.ToolRegistry`, `Vais2.Agents.ParityTests` project.~~
- [x] ~~API freeze sweep, step 1: move `PublicAPI.Unshipped.txt` → `PublicAPI.Shipped.txt` across the seven packages.~~ Done 2026-04-18 — ≈243 entries shipped, strict build + 38/38 tests green.
- [x] ~~API freeze sweep, step 2: dog-food the public surface from `HelloAgent` (tool-calling variant) and `samples/`.~~ Done 2026-04-18 — `HelloAgent` now runs a third segment exercising `ITool` + `IToolRegistry` + `StatefulAgentOptions.ToolRegistry` on both SK and MAF stacks. Two ergonomic findings (`ChatRole` name-collision with MEAI; `SkCompletionProvider` ctor eagerly resolves `IChatCompletionService`) captured in §8 — both deferred as 0.2 decisions, neither blocks preview.
- [x] ~~API freeze sweep, step 3: cut local `0.1.0-preview` NuGet packages and consume from a plain .NET 9 console app.~~ Done 2026-04-18 — `dotnet pack -c Release -p:VersionPrefix=0.1.0 -p:VersionSuffix=preview -o artifacts/packages` produced 7 `.nupkg` + 7 `.snupkg` files; throwaway consumer at `oss/agentic/artifacts/smoketest/` (gitignored; stubbed `Directory.Build.props`/`Directory.Packages.props` to isolate from repo wiring; `NuGet.config` with package-source-mapping pinning `Vais2.Agents.*` to the local feed) restored, built, and ran clean — constructed a representative type from each of the seven packages and printed the provider names.
- [x] ~~Commit + tag: API-freeze commit on OSS repo `main`, annotated tag `v0.1.0-preview`.~~ Done 2026-04-18 — commit `a629213` ("API freeze: 0.1.0-preview — PublicAPI shipped + HelloAgent dog-food", 15 files), tag `v0.1.0-preview` (annotated). Not pushed to any public feed; packages and tag live locally until design-partner round.
- **Phase 1 Milestone 3 progress** (host + persistence + RAG):
  1. [x] ~~`Vais2.Agents.Hosting.Orleans`~~ — M3a done (tag `v0.2.0-alpha`).
  2. [x] ~~`Vais2.Agents.Persistence.Redis` — clustering + grain storage~~ — M3b done (tag `v0.3.0-alpha`). Streams deferred to M3e alongside the neutral agent-event contracts.
  3. [x] ~~`Vais2.Agents.Persistence.Postgres` — clustering + grain storage~~ — M3c done (tag `v0.4.0-alpha`). `IChatHistoryStore` deferred to a later slice once there's a second implementation to shape it against.
  4. [x] ~~`Vais2.Agents.Persistence.VectorData` — MEAI-VectorData-backed `IKnowledgeRetriever` + `KnowledgeRetrievalFilter`~~ — M3d done (tag `v0.5.0-alpha` pending). Filter-pipeline integration means zero Core changes and both adapters (SK + MAF) benefit without modification.
  5. Extended parity, split into sub-milestones:
     - [x] ~~M3e-1: cross-host parity harness (`Vais2.Agents.CrossHostTests`) — same scenario on InMemory vs Orleans+Redis vs Orleans+Postgres → identical history/filter/usage snapshots; plus Orleans rehydration test.~~ — done (tag `v0.6.0-alpha` pending).
     - [x] ~~M3e-2: streaming parity — `IStreamingCompletionProvider` + `CompletionUpdate` in Abstractions; `StatefulAiAgent.StreamAsync`; SK + MAF adapters both implement `IStreamingCompletionProvider`; 6 new Core tests + 3 new Parity tests.~~ — done (tag `v0.7.0-alpha` pending). **Limitation:** filters and resilience pipeline do not apply to streaming turns in v1 (documented). Orleans host proxy streaming is separately deferred (v1 `StatefulAiAgent.StreamAsync` is the concrete-class surface only, not on `IAiAgent` — Orleans host gets it later).
     - M3e-3, split further once we saw the scope:
       - [x] ~~M3e-3a: Abstractions `AgentEvent` + `IAgentEventBus`; `InMemoryAgentEventBus` in Hosting.InMemory; `StatefulAgentOptions.EventBus` + wiring in `StatefulAiAgent.AskAsync` / `StreamAsync` (best-effort, swallows bus failures).~~ — done (tag `v0.8.0-alpha` pending).
       - [x] ~~M3e-3b: `OrleansAgentEventBus` + Orleans serialisation surrogates for the three event subclasses; provider-neutral (consumers pick the stream provider). Memory-streams round-trip tests prove the full wire.~~ — done (tag `v0.9.0-alpha` pending). Redis-specific `UseAgenticRedisStreaming` punted — `Microsoft.Orleans.Streaming.Redis` has no stable 9.x release.
     - [x] ~~M3e-4: multi-agent orchestrator neutral contract — `IAgentOrchestrator` in Abstractions + `SequentialOrchestrator`/`RoundRobinOrchestrator` in Core. Participants are `AgentParticipant(Name, Provider, SystemPrompt?)` — orchestrators drive providers directly, avoiding `IAiAgent`'s per-agent history.~~ — done (tag `v0.10.0-alpha` pending). **Phase 1 Milestone 3 complete.**
- **After M3** we decide whether to push `0.2.0-preview` publicly or iterate on a second design-partner round first.

---

## 8. Milestone log — findings, surprises, decisions

The full dated, append-only record of what each milestone delivered now lives at **[`actor-agents-oss-milestone-log.md`](./actor-agents-oss-milestone-log.md)** — that's the source of truth for chronology, commit hashes, test counts, and per-slice scope decisions. This section is the short version, organised thematically rather than by date, covering the cross-cutting findings that shaped subsequent decisions.

### Parity findings that shaped the architecture

- **Auto-invocation lives at different layers in SK vs MAF.** MAF's `FunctionInvokingChatClient` is a generic pipeline layer over any `IChatClient` — test fakes auto-invoke out of the box. SK's auto-invoke is owned by the concrete `IChatCompletionService` connector (e.g. `OpenAIChatCompletionService`), so fake completion services don't auto-invoke. First surfaced writing M2c parity tests; ultimately motivated flipping ownership of the outer tool-call loop into `StatefulAiAgent` (§9.5 execution-loop pillar). Adapters now translate native tool-call shapes, the agent drives the loop — SK flips `.Auto()` → `.None()`, MAF drops `.UseFunctionInvocation()`.
- **`StatefulAiAgent` passes `_history.ToArray()`, never the live list, to the adapter.** A failing M1 test surfaced a real design property: adapters cannot race on or mutate our state. Cheap snapshot, load-bearing invariant.
- **Cancellation is not a usage event.** `IUsageSink` reports success + failure only — caller-initiated stops don't inflate error metrics.

### Orleans / packaging trivia that cost real time

- **Orleans 9 extension-method namespaces are inconsistent per provider.** Redis extensions live in `Microsoft.Extensions.Hosting`; ADO.NET extensions live in `Orleans.Hosting`. `IPersistentState` lives in `Microsoft.Orleans.Runtime` (not `Sdk`). Trust the compiler error; don't copy usings between persistence packages.
- **Orleans surrogate dispatch is exact-type, not polymorphic-by-base.** Polymorphic `AgentEvent` needed per-subclass converters (`TurnStartedSurrogateConverter`, `TurnCompletedSurrogateConverter`, `TurnFailedSurrogateConverter`, plus later `ToolCallStarted/Completed`, `GuardrailTriggered`, `InterruptRaised`, `HandoffRequested`) sharing an internal helper. Still true in Orleans 10.1.
- **Orleans NuGet packages don't ship the Postgres SQL schema.** Pulled from the source tree and embedded as resources in test projects; runtime consumers run migrations themselves.
- **Orleans Redis `ClearStateAsync` clears content but leaves the key.** Delete tests assert semantic rehydration behaviour (empty history + null prompt post-reactivation), not key existence.
- **Orleans 9 Redis streams had no stable release.** `Microsoft.Orleans.Streaming.Redis` shipped `10.1.0-alpha.1` only; M3e-3b reframed mid-flight from "Redis streams" to provider-neutral `OrleansAgentEventBus` taking an `IClusterClient` + stream-provider name. `UseAgenticRedisStreaming` landed later once the 10.x bump unblocked the Redis streaming provider.
- **Orleans 10 `ORLEANS0014` forbids `ConfigureAwait(false)` in grain code**; `xUnit1030` forbids it in test methods. Different analyzers, same surface-level surprise — `ConfigureAwait(false)` is only for regular async code.

### PublicAPI analyzer reality

- Records + delegates + abstract-record hierarchies blow up the baseline fast (auto-synthesised `<Clone>$`, `Equals(T?)`, `GetHashCode`, `ToString`, operators, `Invoke` on delegates, per-subclass `Equals` on sealed children). First pass of each new package costs 5–15 minutes of baseline churn.
- `*REMOVED*` markers in Unshipped are resolved at freeze time by deleting the matching original Shipped line, not by copying the marker line into Shipped — otherwise RS0024 fires on "shipped API file has removed members".
- `#nullable enable`-only Unshipped stubs are required after freeze — deleting the file fires RS0024.
- `@event` parameter name escapes to `event` in baselines (analyzer operates post-escape).

### NuGet + MSBuild traps

- **CPM + `CentralPackageTransitivePinningEnabled=true`** propagates exact pins (SK's transitive `OpenAI 2.2.0`) into graphs that don't reference them directly — NU1107. Turned off transitive pinning.
- **Dev-machine Syncfusion contamination** in the user's global `NuGet.config` poisoned package resolution; fixed with a repo-local `NuGet.config` that clears global sources.
- **`<clear/>` (or any XML) inside `ItemGroup Label="..."`** is parsed as nested XML and corrupts central-package resolution with broad NU1604/NU1103/NU1701 errors that look nothing like "broken label". Labels must stay plain-text. Lost ~15 min diagnosing.
- **`ActivitySource` is process-global.** Observability tests disable xUnit parallelization (`[assembly: CollectionBehavior(DisableTestParallelization = true)]`) to avoid cross-test activity leakage.
- **Testcontainers 4.11** deprecated parameterless builders — builder-per-container is mandatory now.

### Naming + typing decisions

- **`Vais2.Agents.ChatRole` vs `Microsoft.Extensions.AI.ChatRole`** collision accepted at `0.1.0-preview` (1-line `using` alias), renamed to `AgentChatRole` in the `v0.3.0-preview` clean break. A fresh `Vais2.Agents.IPromptTemplate` vs `Microsoft.SemanticKernel.IPromptTemplate` collision surfaced in the prompt pillar — same class of issue, queued for the next rename window.
- **ADR 0001 — keyed-DI convention is colon-delimited string keys** (`openai:gpt-4o:primary`), not a structured-record key. Structured ergonomics win is small; coupling across every consumer isn't.
- **Abstractions stays Orleans-free.** Every Orleans integration uses `[RegisterConverter]` surrogates in the hosting package rather than attributing Abstractions records. Documented boundary; costs ~40 lines of surrogate boilerplate per serialised type, keeps non-Orleans consumers from ever learning Orleans types exist.
- **`MAF.CreateAIAgent` extension was removed** (not just renamed) between MAF preview and 1.1 — switched to `new ChatClientAgent(...)` directly.

### v0.4 pillar narrowings against the review sketches

- **§9.3 prompt**: `IPromptTemplate` is exposed as a neutral service but NOT on `StatefulAgentOptions` — the composer replaces plain `SystemPrompt` (option (a) merge strategy, avoids merge-order ambiguity), and template rendering belongs to the consumer's composition layer.
- **§9.6 tools**: skipped `IToolApprovalPolicy` — overlaps with `IToolGuardrail.BeforeInvokeAsync` which already returns Pass/Deny/Interrupt.
- **§9.7 orchestration**: skipped the `IHandoff` interface (the record is the data contract) and `IAgentGraphExecutor` / `IAgentGraphBuilder` (too design-speculative — will be shaped by the eventual `GraphOrchestrator` when it lands).
- **§9.8 control plane**: shipped contract-only — no HTTP API / CRDs / YAML / policy engine / identity-provider impl yet; all deferred to cloud-runtime Phase 3. The 7 `IAgentLifecycleManager` verbs are justified as a surveyed universal primitive (AgentCore / Temporal / Restate / Dapr / OpenAI converge on this verb set).
- **§9.9 interop**: outbound MCP + A2A only. Inbound (`McpAgentServer`, `A2AAgentEndpoint` + `OrleansTaskStore`) deferred — unresolved semantic questions on both sides.
- **§9.10 smoketest findings**: `ToolCallCompleted` ctor is `(At, Context, CallId, ToolName, Succeeded, Error, Duration)` — positional `Succeeded`/`Duration`, CallId before ToolName on the related `ToolCallStarted`. Worth knowing when reflection-probing.

### Post-freeze follow-ups (2026-04-19) — interop SDK bumps + tool-using streaming

Three additive follow-ups landed on top of the tagged `v0.4.0-preview` commit (`9c73a4b`), all on local working tree. Both interop adapters were pinned at preview versions under the same "local mirror only has preview X" rationale, which turned out to be imprecise — `E:/nugets` is just the machine's default `globalPackagesFolder` cache, not a curated mirror, and the repo-local `NuGet.config` `<clear/>` + whitelist never blocked nuget.org.

- **MCP `0.1.0-preview.10` → `ModelContextProtocol.Core 1.2.0`** (commit `cf6c883`). Mechanical ~30-LOC adapter rewrite: `IMcpClient` → concrete `McpClient`; `EnumerateToolsAsync` → `ListToolsAsync` (eager `IList`, SDK auto-paginates); `CallToolResponse` + `IReadOnlyList<Content>` → `CallToolResult` + `IList<ContentBlock>` + `TextContentBlock` hierarchy; `serializerOptions` + `progress` folded into a new `RequestOptions` bag. Switched from the `ModelContextProtocol` metapackage to `.Core` to drop unused hosting extensions from the transitive graph.
- **A2A `0.3.1-preview` → `A2A 1.0.0-preview2`** (local working tree, same day). Substantially bigger rewrite because A2A 1.0 reshaped the wire types wholesale (looks protobuf-generated): `AgentMessage` → `Message`; `MessageRole` → `Role`; `Part`/`TextPart`/`DataPart`/`FilePart` polymorphism **collapsed into a single `Part` type with a `PartContentCase` discriminator** (`Text` / `Data` / `Raw` / `Url` / `None`), creation via factory methods `Part.FromText`/`FromData`/`FromRaw`/`FromUrl`; `MessageSendParams` → `SendMessageRequest` (every `IA2AClient` method takes its own `*Request` record); `A2AResponse` polymorphic base → `SendMessageResponse` discriminated union switched on `PayloadCase`; `IA2AClient` grew from 7 → 12 methods; `SendMessageStreamingAsync` renamed to `SendStreamingMessageAsync`, and streaming return type went from `IAsyncEnumerable<SseItem<A2AEvent>>` to `IAsyncEnumerable<StreamResponse>` (SSE parsing absorbed inside the SDK). A2A 1.0 targets `net8.0` + `net10.0` only — consumed via forward-compat under our `net9.0` solution.
- **Tool-using streaming** — `StatefulAiAgent.StreamAsync` now wraps an outer tool-call loop parallel to `AskAsync` instead of being single-turn. Additive surface: `CompletionUpdate` gained a nullable `IReadOnlyList<ToolCallRequest>? ToolCalls` field (last-non-null-wins aggregation, same rule as `ModelId` / token counts). Providers surface model-requested tool calls as a terminal `CompletionUpdate` with `ToolCalls` populated; the outer loop dispatches through `IToolCallDispatcher` (exact same path as `AskAsync` — so `RunBudget`, `IToolGuardrail`, `AgentInterrupt`, and the existing `ToolCallStarted`/`Completed`/`GuardrailTriggered` events all light up automatically), appends the assistant-with-tool-calls + tool-result turns to a working history (session stays clean), and re-enters the stream for the next turn. Consumer surface on the agent stays `IAsyncEnumerable<string>` — tool-call observability flows through the event bus. SK adapter: `FunctionChoiceBehavior.Auto()` → `.None()` on streaming, tool-call fragments accumulated via SK's built-in `FunctionCallContentBuilder`, terminal `CompletionUpdate.ToolCalls` emitted. MAF adapter: accumulates `FunctionCallContent` from `AgentRunResponseUpdate.Contents` by `CallId`, emits terminal update. Streaming-side filter + resilience pipeline stay bypassed (same as v0.4 — known gap, consumers needing them use `AskAsync`).
- 280/280 non-container tests green across all three follow-ups (+7 streaming tool-call tests vs. the 273 pre-streaming baseline). Four packages repacked at `0.4.0-preview` in `artifacts/packages/` for the streaming work (Abstractions + Core + both adapters), plus the two interop packages from the SDK bumps = 13 total packages on disk. Smoketest re-runs clean with an added `CompletionUpdate(ToolCalls: ...)` probe. `v0.4.0-preview` tag stays on `9c73a4b`; all three follow-ups land on top. Decision whether to move the tag, cut a `v0.4.1-preview`, or bundle into `v0.4.1` is pending — all three follow-ups move together.

---

