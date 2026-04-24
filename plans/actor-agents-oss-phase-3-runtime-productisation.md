# Phase 3 — Runtime productisation

Phase 1-2 shipped `Vais.Agents` as a library + CLI + K8s operator. Phase 3 turns it into a **deployable runtime** — a thing users install, deploy agents to, and operate, rather than a thing they `<PackageReference>`.

**Status (2026-04-21):** Pillar A shipped as `v0.16.0-preview` on OSS `main` (commit `1959750`) + Pillar B structurally complete, tag `v0.17.0-preview` awaiting user confirmation. `vais apply -f agent.yaml` + `vais invoke weather --text "hi"` now returns a real model response for declarative agents. Pillars C–F remain; see §Tasks.

## User stories driving Phase 3

Partner feedback reshaped the scope around seven user stories. Restated verbatim for traceability:

1. **US-1** — As a user, I want to install the runtime locally in Docker, or in cloud, using Docker or Kubernetes.
2. **US-2** — As a user, I want to create an agent by code, and deploy it to installed runtime using manifest (like Docker/Kubernetes).
3. **US-3** — As a user, I want to control installed agents from command line tool (like Docker/Kubernetes).
4. **US-4** — As a user, I want to create an agent in a declarative way (YAML), and deploy it, like in US-2.
5. **US-5** — As a user, I want to create an agent graph by code and deploy it, as in US-2.
6. **US-6** — As a user, I want to create an agent graph in a declarative way like in US-4.
7. **US-7** — As a user, I want to create and deploy an agent graph in a declarative way or by code, and the graph can re-use already deployed agents.

The seven reduce to three recurring verbs across two resource kinds: **install** a runtime; **deploy** (agent | graph); **control** (via CLI). Code path vs. YAML path vs. cross-runtime-ref is modality on top.

## What's already shipped (as of v0.15)

Recap against the user stories, so Phase 3 scope is additive, not duplicative:

| Need | Covered by | Notes |
|---|---|---|
| Agent abstractions + execution loop | v0.4 `StatefulAiAgent`, `StatefulAgentOptions`, `RunBudget` | ✅ |
| Tools + guardrails + memory + context | v0.4 `ITool`, `IInput/Output/ToolGuardrail`, `IMemoryStore`, `IContextProvider` | ✅ |
| Orchestration — Sequential / RoundRobin / Handoff | v0.4 Core | ✅ |
| Graph orchestration — BSP, `IAgentGraph<TState>`, checkpoint/resume | v0.9 | ✅ In-process today. |
| Graph manifest YAML loader | v0.9 `Control.Manifests.Yaml` — `YamlAgentGraphManifestLoader` | ✅ US-6 already works in-process. |
| HTTP control plane + idempotency + OpenAPI + SSE streaming | v0.6 + v0.11 + v0.12 `Control.Http.Server` | ✅ |
| OPA policy engine (gate every verb) | v0.14 `Control.Policy.Opa` | ✅ |
| Kubernetes operator + `Agent` CRD reconciler | v0.13 `Control.KubernetesOperator` | ✅ US-1 K8s path partially done — operator ships, but runtime it reconciles against is caller-owned. |
| CLI (`vais apply`, `invoke`, `logs`, `config`, …) | v0.15 `Vais.Agents.Cli` | ✅ US-3 covered. |

## Gaps Phase 3 must close

Per-user-story mapping of the remaining work:

### US-1 — Install runtime locally in Docker / in cloud via K8s

- ❌ **No published runtime container image.** We ship an operator image (`vais-agents-operator:0.13.0-preview`); the operator reconciles against a runtime the caller owns. There's no "vais-agents-runtime" image that bundles `Control.Http.Server` + `Control.InProcess` + SK/MAF adapters + Orleans + OPA into one thing you `docker run`.
- ❌ **No runtime Helm chart.** The operator chart is there; the runtime it talks to doesn't have one.
- ❌ **No docker-compose quick-start.** "Install locally in Docker" today means "write your own host + Dockerfile."

### US-2 — Create agent by code, deploy via manifest

- ❌ **No plugin / assembly-loading model.** The manifest carries `handler.typeName` but the runtime has no wired-up mechanism to load a user-authored `IAiAgent` implementation from a DLL dropped into a folder, an OCI layer, or a sidecar container. The library can do it today only because the consumer's own `dotnet run` process owns the type lookup.
- ❌ **No story for "package my agent as a container image and register it."** Sidecar-per-agent pattern is a plausible future.

### US-3 — Control installed agents from command line

- ✅ Already done in v0.15.
- ❌ **But** — CLI today knows about `kind: Agent` only. `vais apply -f graph.yaml` on a `kind: AgentGraph` manifest should work symmetrically. Dispatch by `kind` inside the apply path.

### US-4 — Create an agent declaratively (YAML) and deploy it

- ✅ Manifest schema already supports `ModelSpec`, `SystemPromptSpec`, `ToolRef`, `McpServerRef`, `GuardrailsSpec`, `OutputSchema`, `Budget`.
- ❌ **No runtime-side instantiation pipeline.** Given a manifest with `Model: { provider: openai, id: gpt-4o }` + `SystemPrompt: { inline: "..." }` + `McpServers: [...]`, nothing in the runtime turns that into a running `StatefulAiAgent`. The fields are records; nothing consumes them on the server side.
- ❌ **No secret-resolution wiring end-to-end.** `SecretRefs` validates in the operator (v0.13) but doesn't feed into the runtime's provider-key resolution.

### US-5 — Create an agent graph by code, deploy it

- ✅ Code-authored graph via `AgentGraphManifest` + `InProcessGraphOrchestrator` works today.
- ❌ **No HTTP verb for graphs.** Control plane exposes `/v1/agents` only — no `/v1/graphs/{id}` CRUD or `/v1/graphs/{id}/invoke`. You can't `POST` a graph manifest to the runtime.
- ❌ **No graph lifecycle manager.** `IAgentLifecycleManager` is per-agent; there's no `IAgentGraphLifecycleManager` to route graph verbs.
- ❌ **No graph CRD.** Operator reconciles `Agent` CRs only.

### US-6 — Create an agent graph in YAML, deploy it

- ✅ YAML loader + JSON loader ship in v0.9's `Control.Manifests.{Json,Yaml}`.
- ❌ Same gap as US-5 — no HTTP surface, no lifecycle manager, no CRD.

### US-7 — Graph re-uses already-deployed agents

- ✅ **In-process:** `GraphNode.Ref: {id, version?}` resolves via `IAgentRegistry.GetAsync` → `IAgentLifecycleManager.InvokeAsync`. A graph and its agent refs co-located on one runtime work today.
- ❌ **Cross-runtime:** graph on runtime A references agents on runtime B — not supported. `InProcessGraphOrchestrator` calls `IAgentLifecycleManager.InvokeAsync` which is always local. Would need a remote-registry adapter + `RemoteAgentInvoker` that dispatches over HTTP to a peer runtime, or reuses the existing `A2ARemoteAgentTool` bridge.

## Phase 3 pillars

Six pillars, roughly in dependency order. Each gets its own spike → findings → pillar plan cycle — this master plan locks the scope + acceptance, not the implementation detail.

### Pillar A — Runtime container + Helm chart + docker-compose (US-1) ✅ **shipped v0.16.0-preview (2026-04-21)**

> Tagged `v0.16.0-preview` on OSS commit `1959750`. Full wrap-up in the [milestone-log entry](./actor-agents-oss-milestone-log.md#2026-04-21--v0160-preview-complete-phase-3-pillar-a--runtime-container--compose--helm--docs); per-PR detail in the [pillar plan](./actor-agents-oss-v0.16-runtime-container-pillar.md). Scope description below preserved as historical record — drifts from what actually shipped are noted inline.

**Scope.** Ship a publishable `vais-agents-runtime` container image + a Helm chart + a docker-compose file. **Orleans-only** — the runtime binary bakes `Hosting.Orleans` unconditionally; `Hosting.InMemory` stays in the library surface for in-process tests + teaching samples but is not a runtime deployment option. One image, one code path; clustering + grain-storage are the configurable knobs.

The container bundles: `Control.Http.Server` + `Control.InProcess` + `Hosting.Orleans` + `Persistence.Redis` + `Persistence.Postgres` + SK + MAF adapters + OpenAPI + idempotency + SSE streaming + OPA wiring hook + optional Langfuse. Durability sidecars that land on Orleans — `OrleansTaskStore` (v0.8), `OrleansCheckpointer` (v0.9), `OrleansIdempotencyStore` (v0.11) — ship enabled by default.

**Two deploy shapes, same image.**

- **Local / single-node.** `UseLocalhostClustering()` + `AddMemoryGrainStorage("Default")`. Zero external deps, grain state lost on restart. Suitable for laptops, CI smoke tests, `docker run` hello-world. Selected via env var `VAIS_HOSTING_MODE=localhost` (default).
- **Production / multi-node.** Redis (default) or Postgres clustering + grain storage. Selected via `VAIS_HOSTING_MODE=clustered` + `VAIS_CLUSTERING_BACKEND=redis|postgres` + connection strings. Multi-replica works because Orleans membership handles it; durability sidecars become effective.

**Key artefacts** (status: shipped, with drifts from the original scope below noted inline).

- `src/Vais.Agents.Runtime.Host/` — WebApplication project. In-repo only; not a published NuGet. Configurable via `appsettings.json` + env vars for clustering backend (localhost / Redis / Postgres), grain-storage backend, OPA baseUrl, Langfuse project, OTel exporter endpoint. **Shipped.** Composition split across `Program.cs` / `CompositionRoot.cs` / `RuntimeOptions.cs` / `OrleansActiveHealthCheck.cs` so tests drive the composition root without booting Orleans.
- `src/Vais.Agents.Runtime.Host/Dockerfile` — multi-stage build → `vais-agents-runtime:0.16.0-preview`. Non-root user, `/healthz` + `/readyz` on port 8080. **Shipped.**
- `deploy/helm/vais-agents-runtime/` — Helm chart parallel to the operator chart. **Shipped** with drift: (1) `grainStorage` knob consolidated into `clustering` since Orleans 10.x shares the connection for clustering + grain storage; splitting them is a footgun. (2) `clustering.existingSecret` added for production Secret-based wiring. (3) `appsettings.Production.json` ConfigMap dropped — env vars cover every v0.16 knob. (4) **No Redis subchart** — consumer brings their own. Multi-replica enabled from day one.
- `deploy/compose/*.yml` — **shipped** as 2 bases (`localhost`, `clustered`) + 3 overlays (`opa`, `langfuse`, `otel`) instead of the planned "one file + overlay." Compose orthogonally via `-f` layering; all 7 base-overlay pairings + the 4-way combined overlay validated with `docker compose config`.
- `docs/guides/install-the-runtime-locally.md` + `docs/guides/deploy-the-runtime-to-kubernetes.md` + `docs/reference/runtime-configuration.md` — **shipped.** The reference page was added on top of the plan's two-guide scope to keep env / appsettings / Helm-values cross-referenced in one place.

**Acceptance (shipped state).**

- ✅ `docker run vais-agents-runtime:0.16.0-preview` → `/healthz` returns 200 on port 8080 within ~5s (localhost mode; no external deps).
- ✅ `curl http://localhost:8080/openapi/v1.json` returns the v0.11 spec; unit tests prove `IIdempotencyStore` resolves to `OrleansIdempotencyStore` (not in-memory).
- ⚠️ Multi-replica clustered smoke is **documented** in `deploy/compose/README.md` with an override-file recipe (base file publishes 8080, scale>1 drops the port) and verified locally; not CI-automated. Kind integration test deferred to Pillar F.
- ✅ `helm install vais-agents-runtime deploy/helm/vais-agents-runtime/ --set hosting.mode=clustered --set clustering.existingSecret=vais-redis --set replicaCount=3` — validated via `helm lint` + 6 representative value combinations rendered cleanly.
- ✅ All three durability sidecars (`OrleansTaskStore` / `OrleansCheckpointer` / `OrleansIdempotencyStore`) engage automatically — the composition-root unit tests guard the registration order that makes them effective (`Composition_Registers_OrleansBacked_*_Store`).

**Image size.** Measured ~148 MB (alpine, Orleans + Redis client + Postgres client + OTel exporters + NO SK / MAF — the adapters are deferred to Pillar B since no manifest-driven instantiation runs in v0.16 and shipping them now is dead weight). Dockerfile's `ARG BASE_IMAGE` lets Pillar F flip to chiseled (~98 MB) in a one-line change.

**Defers to Pillar B.** Confirmed in practice: `vais apply` succeeds (the Orleans-backed registry persists the manifest), but `vais invoke` returns `501 urn:vais-agents:agent-not-instantiable`. This is documented behaviour across the install guides + the Helm NOTES banner + the docker-compose README.

### Pillar B — Declarative agent instantiation (US-4, enables US-2)

**Scope.** Turn a `AgentManifest` into a running `StatefulAiAgent` inside the runtime. No C# consumer code required for pure LLM-plus-tools-plus-guardrails agents.

**Key artefacts.**

- `Vais.Agents.Runtime.Instantiation` (new package in `src/`) — `AgentInstantiator : IAgentHandlerFactory`. Given a manifest, returns an `IAiAgent` wired with the right `ICompletionProvider` + `ITool`s + `IInput/Output/ToolGuardrail`s + `ISystemPromptComposer` + `RunBudget` + `OutputSchema`.
- **Model resolution:** `ModelSpec.Provider` → provider-specific factory. Ships `OpenAIProviderFactory` + `AnthropicProviderFactory` + `AzureOpenAIProviderFactory` at launch; extensible via `IModelProviderFactory` DI registration. Factory consumes `ISecretResolver` for API keys.
- **Tool resolution:** `ToolRef.Source` parsing — `"static:<name>"` → local registry; `"mcp:<server-name>"` → `McpToolSource` pointing at the named server; `"a2a:<agent-name>"` → `A2ARemoteAgentTool`.
- **SystemPrompt resolution:** `SystemPromptSpec.Inline` → literal; `SystemPromptSpec.TemplateRef` → DI-resolved `IPromptTemplate`; `SystemPromptSpec.FileRef` → read from configurable prompts directory.
- **Guardrails resolution:** `GuardrailsSpec.{Input,Output,Tool}` arrays of refs → DI-resolvable guardrail instances by type name. Ships four built-ins (length cap, regex allowlist, regex denylist, LLM-as-judge); extensible.
- **Secret resolver composite:** `EnvironmentSecretResolver` + `FileSecretResolver` + `KubernetesSecretResolver` (when running in K8s; uses downward-API-mounted Secrets). Already-partial contract from `Control.Abstractions`.
- `docs/concepts/declarative-agents.md` + `docs/guides/author-an-agent-in-yaml.md`.

**Acceptance.**

A pure-YAML agent with no consumer code deploys and runs:

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: weather-assistant
  version: "1.0"
spec:
  description: Answers weather questions using a weather MCP server.
  model:
    provider: openai
    id: gpt-4o
    secretRef: openai-api-key
  systemPrompt:
    inline: |
      You help users understand the weather.
      Be concise; always cite the tool's response timestamp.
  mcpServers:
    - name: weather-server
      url: http://weather-mcp.example.com
  guardrails:
    input:
      - name: LengthLimit
        config: { maxChars: 2000 }
  budget:
    maxTurns: 5
    maxDuration: 30s
```

```bash
vais apply -f weather-assistant.yaml
vais invoke weather-assistant --text "What's the weather in Tokyo?"
# ← model-generated answer using tools from the MCP server
```

### Pillar C — Plugin model for code-authored agents (US-2 primary)

**Scope.** Users who write `IAiAgent` / `IStreamingAiAgent` implementations in .NET can package them as DLL plugins + drop into the runtime without rebuilding the runtime image.

**Key decision — three candidate shapes:**

1. **DLL plugin folder.** `/plugins/<name>.dll` + a descriptor file advertising which `AgentHandlerRef.TypeName` strings the assembly provides. `AssemblyLoadContext` isolation.
2. **OCI image layer.** Agent assemblies ship as a container image layered on top of the runtime base image. Composes with multi-stage builds.
3. **Sidecar container.** Each agent is its own container, runtime routes to it via loopback A2A. Highest isolation, most overhead.

Spike (`plans/actor-agents-oss-v0.17-plugin-model-spike.md`) picks one or mandates two. Probable answer: **(1) at launch, (3) in a later pillar** — DLL plugins for the "my .NET colleagues wrote a handler" case; sidecar for the "this agent is in Python" / "we need hermetic isolation" case.

**Key artefacts (assuming DLL-plugin path wins the spike).**

- `Vais.Agents.Runtime.Plugins` — `AssemblyPluginLoader` + `PluginDescriptor` record + `ApplicationLoadContext` isolation wrapping.
- Plugin descriptor format: `plugin.json` alongside the DLL advertising exported handler types + the `Vais.Agents.Abstractions` version the plugin was built against.
- Runtime `appsettings.json`: `Plugins.Directory` (default `/plugins` in the container image).
- `Dockerfile` update: `COPY --from=build /publish/plugins /plugins` convention for consumers who build their own image on top.
- Sample: `samples/PluginAgentWeather/` — code-authored agent packaged as a plugin, with a `Dockerfile` showing the overlay pattern.
- `docs/concepts/runtime-plugins.md` + `docs/guides/package-an-agent-as-a-plugin.md`.

**Acceptance.**

```bash
# Build a plugin containing MyApp.WeatherAgent : IAiAgent
dotnet publish MyWeatherPlugin.csproj -o ./plugins/weather

# Mount + start the runtime
docker run -v $PWD/plugins:/plugins vais-agents-runtime:0.16.0-preview

# Apply a manifest that references the plugin's type
vais apply -f - <<'EOF'
apiVersion: vais.agents/v1
kind: Agent
metadata: { id: weather, version: "1.0" }
spec:
  handler:
    typeName: MyApp.WeatherAgent
    assemblyName: MyWeatherPlugin
EOF

# Invoke — runtime loads the plugin, instantiates WeatherAgent, runs it
vais invoke weather --text "Weather in Tokyo?"
```

### Pillar D — Graph as a first-class deployable (US-5, US-6)

**Scope.** Promote `AgentGraphManifest` from "record the consumer loads themselves" to "resource the runtime registers, reconciles, and invokes." Parallel to Agent: CRD + HTTP verbs + CLI dispatch + operator reconciliation.

**Key artefacts.**

- **Contract:** `IAgentGraphLifecycleManager` in `Vais.Agents.Control.Abstractions` — seven verbs (Create / Invoke / Cancel / Query / Update / Evict + a graph-specific Resume for interrupt/resume flows).
- **Runtime:** `AgentGraphLifecycleManager` in `Vais.Agents.Control.InProcess` — registers graphs in `IAgentGraphRegistry`, instantiates `InProcessGraphOrchestrator<TState>` on invoke, threads `IGraphCheckpointer` for durable resume.
- **HTTP surface:** `/v1/graphs` CRUD + `/v1/graphs/{id}/invoke` + `/v1/graphs/{id}/invoke/stream` (SSE event stream — `AgentGraphEvent` hierarchy on the wire, reuses ADR 0004's taxonomy pattern) + `/v1/graphs/{id}/resume`.
- **CRD:** `vais.io/v1alpha1/AgentGraph` — parallel to `Agent`. Schema mirrors `AgentGraphManifest` (entry, nodes, edges, maxSteps, state schema). Operator reconciles alongside `Agent` CRs; spec-hash / conditions / phase enum shape matches `Agent`.
- **CLI:** `vais apply -f graph.yaml` dispatches on `kind`. `vais get graphs`, `vais invoke-graph <id> --initial-state @state.json`, `vais graph-logs <id>`.
- `docs/concepts/graph-as-deployable.md` + `docs/guides/deploy-a-graph-to-the-runtime.md`.

**Acceptance.**

```bash
# Apply a graph
vais apply -f support-triage.yaml    # kind: AgentGraph
# ✓ support-triage:1.0 applied (created)

# Invoke
vais invoke-graph support-triage --initial-state '{"user_query": "I need a refund"}'
# { "category": "billing", "final": "Refund approved, transaction id: ..." }

# Watch events
vais graph-logs support-triage --session run-123
# [start]  run=run-123 entry=classify
# [node→]  classify ...
# [edge]   classify → handle-billing ...
```

### Pillar E — Cross-runtime agent refs in graphs (US-7 primary)

**Scope.** A `GraphNode.Ref: {id, version?}` in a graph on runtime A can resolve to an agent deployed on runtime B. Enables cross-org / cross-tenant / cross-region agent composition without each runtime needing to redeploy everything.

**Two candidate mechanisms:**

1. **Explicit remote-runtime config.** Runtime A's config lists peer runtimes (`remoteRuntimes: [{name: prod-b, baseUrl: https://…, auth: …}]`). Graph nodes annotate their ref with `runtime: prod-b` when they want to cross boundaries.
2. **A2A-based discovery.** Leverage the v0.8 A2A outbound adapter + `.well-known/agent-card.json`. `GraphNode.Ref: {id, via: "a2a", url: "https://…"}`. The graph orchestrator wraps the remote via `A2ARemoteAgentTool` semantics.

Spike (`plans/actor-agents-oss-v0.20-cross-runtime-refs-spike.md`) picks one or supports both. Probable answer: **both** — explicit remote config for trusted same-org peers (tight latency + bearer auth), A2A for third-party / external services (richer card metadata + existing security model).

**Key artefacts.**

- `IRemoteAgentRegistry` — contract.
- `HttpRemoteAgentRegistry` — explicit-config implementation. Reads `remoteRuntimes:` config, resolves `GraphAgentRef` → `Vais.Agents.Control.Http.Client.AgentControlPlaneClient` instance.
- `A2ARemoteAgentRegistry` — A2A-based implementation. Uses `A2ARemoteAgentTool` under the hood.
- `InProcessGraphOrchestrator<TState>` extension — accepts `IRemoteAgentRegistry` composite resolver, falls through local registry first, then remote-configured ones.
- `GraphAgentRef` (in `Vais.Agents.Abstractions`, v0.9) grows an optional `Runtime` field (`string?`) and optional `A2AUrl` field — additive; unshipped → shipped edit per `PublicAPI.*.txt`.
- `docs/concepts/cross-runtime-graphs.md` + `docs/guides/compose-a-graph-across-runtimes.md`.

**Acceptance.**

Three agents deployed on three separate runtimes — billing agent on `runtime-b`, technical agent on `runtime-c`, classifier on `runtime-a` (which also hosts the graph):

```yaml
apiVersion: vais.agents/v1
kind: AgentGraph
metadata: { id: support-triage, version: "1.0" }
spec:
  entry: classify
  nodes:
    - id: classify
      kind: Agent
      ref: { id: classifier, version: "1.0" }                       # local to runtime-a
    - id: handle-billing
      kind: Agent
      ref: { id: billing-agent, version: "2.0", runtime: prod-b }   # on runtime-b
    - id: handle-tech
      kind: Agent
      ref: { id: tech-agent, via: a2a, url: "https://remote-vendor/agents/tech-agent" }
  edges: [...]
```

```bash
vais invoke-graph support-triage --initial-state '{"user_query":"..."}'
```

Graph runs on runtime-a; classify invokes locally; handle-billing routes to runtime-b via HTTP; handle-tech routes via A2A. Audit trail on runtime-a shows the cross-runtime hops; each receiving runtime sees a normal invoke.

### Pillar F — Phase 3 docs + samples + polish

**Scope.** End-to-end narrative documentation + a curated set of runnable samples that exercise every user story top-to-bottom.

**Key artefacts.**

- `docs/getting-started/install-the-runtime.md` — five-minute docker-compose walkthrough.
- `docs/getting-started/deploy-your-first-agent.md` — one YAML file, one `vais apply`, first invoke.
- `docs/tutorials/from-zero-to-graph-in-20-minutes.md` — long-form tutorial.
- End-to-end samples:
  - `samples/runtime-docker-compose/` — install locally (US-1).
  - `samples/declarative-agent-yaml/` — pure-YAML agent, no code (US-4).
  - `samples/code-agent-plugin/` — code-authored plugin agent (US-2).
  - `samples/graph-code-authored/` — graph built + deployed from C# (US-5).
  - `samples/graph-yaml-authored/` — graph built + deployed from YAML (US-6).
  - `samples/graph-cross-runtime/` — graph composing agents on three runtimes (US-7).
- Sweeping doc update: `docs/concepts/architecture.md` grows a "Runtime" section with the Runtime.Host + Plugins + Instantiation layering.
- Packages reference gets the new `Vais.Agents.Runtime.*` rows.

**Acceptance.**

Fresh hire with zero Vais.Agents context follows `docs/tutorials/from-zero-to-graph-in-20-minutes.md` and arrives at a running cross-runtime graph end-to-end. If they get stuck, the stuck point drives a doc bug.

## Dependency graph + sequencing

```
Pillar A (runtime container + chart + compose)
   │
   ├──▶ Pillar B (declarative agent instantiation)  ◀──┐
   │                                                    │  (B and C are parallel
   └──▶ Pillar C (plugin model for code agents)   ◀──┤   but both depend on A)
                                                        │
Pillar D (graph as deployable) ◀──── depends on A + B (B handles per-node instantiation)
                                                        │
Pillar E (cross-runtime refs in graphs) ◀──── depends on D
                                                        │
Pillar F (docs + samples) ◀──── depends on A through E
```

Rough sizing — each pillar is a normal-shape spike → findings → pillar plan → implementation cycle. Expect 1-2 weeks per pillar for the implementation; docs+samples add another week. Total Phase 3 ≈ 8-12 weeks.

Version tags tentative:

| Pillar | Version |
|---|---|
| A | `v0.16.0-preview` |
| B | `v0.17.0-preview` |
| C | `v0.18.0-preview` |
| D | `v0.19.0-preview` |
| E | `v0.20.0-preview` |
| F | rolled into `v0.20` |

Promotion to `v1.0-preview` is a separate decision after Phase 3 settles — Phase 3 stays pre-alpha throughout.

## Non-goals for Phase 3

- **Multi-region / leader-election.** Single-region, single-leader runtime only. Deferred.
- **Identity-provider implementations.** `IAgentIdentityProvider` remains contract-only. Keycloak / Auth0 / Entra impls defer to a later pillar.
- **Dynamic plugin hot-reload.** Plugins load at runtime start; changing `/plugins` requires restart. Hot-reload is a nice-to-have; not Phase 3 scope.
- **Non-.NET plugins.** DLL plugins are .NET-only in v0.18. Python / Node agent hosting happens via the sidecar-agent story (deferred) or via A2A cross-runtime refs (Pillar E).
- **Visual-designer / UI.** Dashboard out of scope. CLI + `kubectl` + Grafana are the surface.
- **Samples migration.** The housekeeping-samples plan stays deferred; Pillar F's end-to-end samples replace most of what the samples plan covered.

## Success criteria

Phase 3 is done when a partner can follow the getting-started tutorial and end up with:

1. A runtime running in docker-compose (US-1 local).
2. A pure-YAML agent deployed and invoked via CLI (US-3, US-4).
3. A code-authored plugin agent deployed and invoked via CLI (US-2, US-3).
4. A YAML-authored graph composing both above (US-6, US-7 local).
5. The same graph running against three remote runtimes (US-5 optional, US-7 cross-runtime).
6. K8s deployment of the runtime via Helm + operator reconciling CRs (US-1 cloud).

All six demonstrable in one afternoon by a newcomer without touching internals.

## Tasks

Per-pillar task lists. Each pillar opens its own spike / findings / pillar plan inside `plans/` — this file stays high-level.

### Pillar A — Runtime container ✅ **complete 2026-04-21**

See the [milestone-log entry](./actor-agents-oss-milestone-log.md#2026-04-21--v0160-preview-complete-phase-3-pillar-a--runtime-container--compose--helm--docs) for the wrap-up and the [pillar plan](./actor-agents-oss-v0.16-runtime-container-pillar.md) for per-PR detail.

- [x] Spike: settle clustering-backend default (Redis vs Postgres), docker-compose three-tier shape, OPA wiring shape, durability-sidecar defaults. `plans/actor-agents-oss-v0.16-runtime-container-spike.md`.
- [x] Findings + pillar plan: `plans/actor-agents-oss-v0.16-runtime-container-{findings,pillar}.md`.
- [x] Implement `src/Vais.Agents.Runtime.Host/` project — Orleans-only, mode switch (`localhost` vs `clustered`) via env var.
- [x] Wire durability sidecars by default: `AddOrleansA2ATaskStore`, `AddOrleansGraphCheckpointer`, `AddOrleansIdempotencyStore`. Ordering locked by 3 composition-root unit tests.
- [x] Dockerfile. **CI image build deferred to Pillar F** — local build + docker-compose recipes are the v0.16 ship story.
- [x] `deploy/helm/vais-agents-runtime/` chart. **Drift**: `grainStorage` consolidated into `clustering` (Orleans 10.x shares the connection). **No Redis subchart** — consumer brings their own.
- [x] `deploy/compose/docker-compose.{localhost,clustered,opa,langfuse,otel}.yml` — 2 bases + 3 overlays + README + example.rego.
- [x] `docs/guides/install-the-runtime-locally.md` + `docs/guides/deploy-the-runtime-to-kubernetes.md` + `docs/reference/runtime-configuration.md`.
- [~] Multi-replica smoke test: documented in the compose README (`--scale runtime=3` + override.yml for port drop); not CI-automated. Kind integration test also deferred to Pillar F polish.
- [x] Merged to OSS `main` as two commits (`6643b82` docs housekeeping + `1959750` v0.16.0-preview Pillar A). OSS repo has no remote yet; commits are local-only.
- [x] Tag `v0.16.0-preview` — created annotated on commit `1959750` (2026-04-21).

### Pillar B — Declarative agent instantiation ✅ **complete 2026-04-21**

See the [milestone-log entry](./actor-agents-oss-milestone-log.md#2026-04-21--v0170-preview-complete-phase-3-pillar-b--manifest-driven-agent-instantiation) for the wrap-up and the [pillar plan](./actor-agents-oss-v0.17-manifest-instantiation-pillar.md) for per-PR detail.

- [x] Spike + findings + pillar plan for `v0.17`. `plans/actor-agents-oss-v0.17-manifest-instantiation-{spike,findings,pillar}.md`.
- [x] `src/Vais.Agents.Runtime.Instantiation/` package. 8 contracts + 7 impls + 4 DI extensions + 46 unit tests.
- [x] Model-provider factories (OpenAI + Anthropic + AzureOpenAI via MEAI `IChatClient`) + `IModelProviderFactory` + `AddBuiltinModelProviders`.
- [x] Tool-ref resolution (`static:` / `mcp:` / `a2a:`) — `static:` fully materialized via `IStaticToolRegistry`; `mcp:` + `a2a:` validate declaration today, lazy tool-source materialization deferred to v0.17.1.
- [x] SystemPrompt resolver (inline / templateRef / fileRef) — all three shapes supported; `IPromptTemplateRegistry` + `IPromptFileLoader` added beyond findings scope.
- [x] Guardrails resolver (4 built-ins spread across 6 factories — LengthCap Input + RegexAllowlist × 2 + RegexDenylist × 2 + LlmAsJudge Output) + DI extensibility via `AddSingleton<IGuardrailFactory>`.
- [x] Secret-resolver composite (env + file) — reuses v0.6 `CompositeSecretResolver.CreateDefault()`. K8s covered via `file://` against projected-token volumes.
- [x] `docs/concepts/declarative-agents.md` + `docs/guides/author-an-agent-in-yaml.md` shipped. `ship-a-guardrail.md` + `ship-a-custom-model-provider.md` + `manifest-schema.md` reference deferred to v0.17.1 — XML docs + OpenAPI schema cover the gap.
- [x] Integration test: `ManifestInstantiationIntegrationTests` — end-to-end apply-translate-invoke-returns-response + register-v2+invalidate+invoke-picks-up-new-prompt against a fake `ICompletionProvider`. Skips TestCluster for speed; Orleans grain path covered by existing `CrossHostTests`.
- [x] Tag `v0.17.0-preview` — annotated on OSS commit `2b2bb5d` (2026-04-21). Two-commit merge on OSS `main`: `163a2e9` (library layer) + `2b2bb5d` (runtime host + docs).

### Pillar C — Plugin model

- [x] Spike: DLL-plugin vs OCI-layer vs sidecar. `plans/actor-agents-oss-v0.18-plugin-model-spike.md` — locked DLL-plugin + per-plugin `AssemblyLoadContext` + shared-types carve-out.
- [x] Findings + pillar plan (`plans/actor-agents-oss-v0.18-plugin-model-findings.md`, `plans/actor-agents-oss-v0.18-plugin-model-pillar.md`).
- [x] `src/Vais.Agents.Runtime.Plugins/` package (new, 9 source files + PublicAPI). `IAgentHandlerFactory` + `VaisPluginAttribute` in Abstractions; `StatefulAgentOptions.Agent` in Core; `IManifestApplyDiagnosticsSink` in Control.Abstractions; 2 new translator URNs (`plugin-factory-throw`, `handler-and-declarative-fields-both-set`); `AiAgentGrain.OnActivateAsync` widened to prefer `options.Agent`.
- [x] `PluginDescriptor` + `AssemblyPluginLoader` + `PluginAssemblyLoadContext` with shared-types carve-out + `AssemblyDependencyResolver`-backed transitive resolution + `DefaultHandlerFactory<T>` auto-wrap + `VaisRuntimeAbi.CurrentVersion = "0.18"` major.minor match.
- [x] Runtime host wiring: `RuntimeOptions.PluginsDirectory` + `VAIS_PLUGINS_DIRECTORY` env (default `/var/lib/vais/plugins`; empty string disables); `CompositionRoot` registers `AddAgentPlugins` before `AddAgentManifestInstantiator`; `appsettings.json` documents `Plugins:Directory`.
- [x] Sample: `samples/PluginAgentWeather/` with `MyApp.WeatherAgent` class library + `[assembly: VaisPlugin]` + overlay `Dockerfile.overlay` + end-to-end README.
- [x] `docs/concepts/runtime-plugins.md` + `docs/guides/package-an-agent-as-a-plugin.md` + sweeps across `declarative-agents.md`, `architecture.md` (new Plugin tier + 27-package bump), `packages.md`, `runtime-configuration.md`, `problem-details-urns.md`, `index.md`, `installation.md`.
- [x] Tests: 23 Runtime.Plugins.Tests, 13 new PluginTranslationTests (59 total Runtime.Instantiation.Tests), 6 new composition-root tests + 2 integration tests (20 total Runtime.Host.Tests). All 22 test projects green; full solution 0 warnings / 0 errors.
- [x] Milestone entry in `plans/actor-agents-oss-milestone-log.md`.
- [x] Tag `v0.18.0-preview` — annotated on OSS commit `454ec33` (2026-04-21). Two-commit bundle on OSS `main`: `464a8b6` (library layer) + `454ec33` (runtime host + sample + docs).

### Pillar D — Graph as deployable

- [x] Spike + findings + pillar plan for `v0.19`.
- [x] `IAgentGraphLifecycleManager` contract in `Control.Abstractions`.
- [x] `AgentGraphLifecycleManager` in `Control.InProcess` + `IAgentGraphRegistry`.
- [x] HTTP surface — `/v1/graphs/{id}` CRUD + invoke + invoke/stream + resume.
- [x] CRD: `vais.io/v1alpha1/AgentGraph` + operator reconciler parallel to `Agent`.
- [x] CLI dispatch on `kind` + new `invoke-graph` / `graph-logs` verbs.
- [x] `docs/concepts/graph-as-deployable.md` + `docs/guides/deploy-a-graph-to-the-runtime.md`.
- [x] Tag `v0.19.0-preview` — annotated on OSS commit `b62ff95` (2026-04-21).

### Pillar E — Cross-runtime graph refs

- [x] Spike: explicit-config chosen. `plans/actor-agents-oss-v0.20-cross-runtime-refs-spike.md` (2026-04-21).
- [x] Findings + pillar plan: `plans/actor-agents-oss-v0.20-cross-runtime-refs-{findings,pillar}.md` (2026-04-21).
- [x] `IAgentRemoteInvoker` interface + `HttpAgentRemoteInvoker` implementation (PR 1, commit `a8aa64c`, 2026-04-21).
- [x] `GraphAgentRef` grows `RuntimeUrl?: string` (additive, null = local) (PR 1, 2026-04-21).
- [x] One branch per orchestrator (`InProcessGraphOrchestrator` + MAF `GraphNodeExecutor`) (PR 1, 2026-04-21).
- [x] Manifest loader + YAML + CRD projector: additive `runtimeUrl` field (PR 2, 2026-04-21).
- [x] `docs/concepts/cross-runtime-graphs.md` + `docs/guides/compose-a-graph-across-runtimes.md` (PR 3, 2026-04-21).
- [x] Tag `v0.20.0-preview` (PR 3, 2026-04-21).

### Pillar F — Phase 3 docs + samples ✅ **complete 2026-04-21**

See the [milestone-log entry](./actor-agents-oss-milestone-log.md#phase-3-complete--pillar-f-docs--samples-2026-04-21) for the wrap-up.

- [x] `docs/getting-started/install-the-runtime.md` (docker-compose five-min walkthrough).
- [x] `docs/getting-started/deploy-your-first-agent.md`.
- [x] `docs/tutorials/from-zero-to-graph-in-20-minutes.md`.
- [x] Six end-to-end samples (`runtime-docker-compose`, `declarative-agent-yaml`, `code-agent-plugin`, `graph-code-authored`, `graph-yaml-authored`, `graph-cross-runtime`).
- [x] Sweeping updates to `docs/concepts/architecture.md` (v0.19 graph control-plane tier section), `docs/reference/packages.md` (version bump to 0.20.0-preview, v0.19 + v0.20 scenario bundles), `samples/README.md` (6 new rows + count bump to 27).
- [~] Internal walkthrough with a newcomer — deferred (no external reviewers available; doc coverage is complete and coherent).
- [x] **Phase 3 complete.**

## Open questions to resolve in spikes

- **Prod clustering backend default — Redis vs Postgres.** Both ship. Helm `--set clustering.backend=redis` is the obvious default given our existing Redis investment (membership + streams) and the docker-compose shape; Postgres avoids pulling in a second datastore when the consumer already runs one. Spike A picks a documented default + a decision matrix for choosing.
- **docker-compose three-tier shape.** What's the minimum viable local deployment? Options: (a) runtime alone (localhost mode), (b) runtime + Redis (clustered mode, single-node), (c) runtime + Redis + OPA sidecar. Spike A settles on the canonical file + what the overlays look like.
- **Plugin descriptor format.** `plugin.json` vs attribute-decorated assemblies vs both. Spike C.
- **Graph resume semantics over HTTP.** Does `/v1/graphs/{id}/resume` take a checkpoint-id? A full checkpoint payload? How does the caller discover the checkpoint id after an interrupt on the streaming-invoke path? Spike D.
- **Cross-runtime identity propagation.** When runtime A's graph invokes runtime B's agent, does B see A's bearer token unchanged (delegation), A's service-account token (pod-to-pod), or a short-lived exchange-derived token (OIDC token exchange)? Spike E.
- **Secret refs at runtime vs operator.** v0.13 validates secretRefs in the operator but doesn't wire values. Does Pillar B's secret resolver use the K8s API directly (bypassing the operator), or does the operator project resolved values into the manifest envelope it `POST`s to the runtime? Spike B.

## Progress log

- 2026-04-21 — Plan created. Phase 3 re-framed around seven partner-feedback user stories. Maps to six pillars (A-F) in dependency order: runtime container, declarative instantiation, plugin model, graph-as-deployable, cross-runtime refs, docs + samples. Tentative version span `v0.16` → `v0.20`, estimated 8-12 weeks. Samples plan from the housekeeping phase stays deferred — Pillar F's end-to-end samples cover the same surface.

- 2026-04-21 — Pillar A scope tightened: **Orleans-only runtime binary**, not a dual InMemory/Orleans image. `Hosting.InMemory` stays as a library package for in-process tests + teaching samples but is not a deployment option for the runtime. Two deploy shapes, same image: localhost-mode (`UseLocalhostClustering` + memory grain storage, zero external deps) for laptops + CI; clustered-mode (Redis or Postgres clustering + grain storage) for prod K8s. Durability sidecars — `OrleansTaskStore` / `OrleansCheckpointer` / `OrleansIdempotencyStore` — ship enabled by default so in-flight state survives restart. Image size grows to ~150 MB (+~2-3s cold start for silo activation) — accepted as the cost of a productised runtime. Spike-A open questions renormalised: lose "hosting-mode default" (decided); gain "Redis vs Postgres default for clustering" + "canonical docker-compose three-tier shape". **Pending**: Pillar A spike.
