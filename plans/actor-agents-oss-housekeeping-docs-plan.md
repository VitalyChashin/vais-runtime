# Housekeeping — documentation update plan (v0.7 → v0.15)

Scope: bring `oss/agentic/docs/` to parity with the nine pillars that landed after v0.6. Inter-phase housekeeping — no new code. Companion to [`actor-agents-oss-housekeeping-samples-plan.md`](./actor-agents-oss-housekeeping-samples-plan.md). Created 2026-04-20.

---

## Current state

**Docs tree as of v0.15.0-preview** (`oss/agentic/docs/`):

```
docs/
├── adr/
│   ├── 0001-keyed-ichatclient-di-convention.md
│   ├── 0002-otel-genai-conventions.md
│   └── index.md
├── concepts/               # 12 pages — stop at v0.6
│   ├── architecture.md
│   ├── context.md
│   ├── control-plane.md
│   ├── execution-loop.md
│   ├── guardrails.md
│   ├── interop.md          # outbound MCP + A2A only
│   ├── observability.md
│   ├── orchestration.md
│   ├── persistence.md
│   ├── prompt.md
│   ├── session.md
│   └── tools.md
├── getting-started/        # 3 pages
│   ├── choosing-a-stack.md
│   ├── hello-agent.md
│   └── installation.md
├── guides/                 # 10 how-tos — pre-v0.6
│   ├── add-input-output-guardrails.md
│   ├── add-postgres-persistence.md
│   ├── add-redis-persistence.md
│   ├── delegate-to-a2a-remote-agent.md
│   ├── deploy-otel-and-langfuse.md
│   ├── expose-mcp-tools-to-an-agent.md
│   ├── run-on-orleans-locally.md
│   ├── stream-with-tools.md           # library streaming; not v0.12 HTTP SSE
│   ├── wire-a-custom-tool.md
│   └── wire-rag-via-vectordata.md
├── reference/              # 4 pages
│   ├── budget.md
│   ├── events.md
│   ├── packages.md                    # enumerates 13 packages — pre-v0.7
│   └── telemetry-keys.md
└── index.md                           # enumerates 12 concepts — pre-v0.7
```

**Coverage gap**: nothing authored for v0.7 (MCP inbound), v0.8 (A2A inbound), v0.9 (graph orchestration), v0.10 (streaming filters + resilience), v0.11 (OpenAPI + Idempotency-Key), v0.12 (SSE streaming Invoke), v0.13 (Kubernetes operator), v0.14 (OPA policy engine), v0.15 (CLI).

**Not out of scope but not this pillar**: the `contracts/opa-input-schema.md` doc already ships alongside the OPA pillar at the repo root; the `deploy/helm/vais-agents-operator/README.md` + `deploy/README.md` + `samples/opa-*/README.md` already ship alongside v0.13/v0.14. These cross-link into the main docs tree but don't need rewrites here.

---

## Principles

- **Concept pages are the definitive how-and-why.** One page per pillar, sections: *What it is → Core types → Wiring → Extension points → Known limitations*. Depth comparable to existing `concepts/orchestration.md` (~200-400 lines).
- **Guides are a straight-line narrative.** Each guide walks through one user-story end-to-end: starting state → commands → code → expected output → next steps. No theory, no extensive prose.
- **Reference is lookup-friendly.** Tables; flag lists; shape schemas; error-code mappings. Alphabetised; no narrative.
- **Update `index.md` every time a concept page is added** — the index is the discovery surface.
- **Follow the existing voice.** Tight prose; second-person ("you wire it via …"); code fences with file paths; no marketing.
- **Cross-link liberally.** Concept ↔ guide ↔ reference ↔ sample (samples plan covers the reverse direction).
- **Update the samples table in `samples/README.md`** every time a new sample lands (done by the samples plan; docs plan adds a "sample" column to each concept page's intro).

---

## Pillar-by-pillar coverage map

### v0.7 — MCP inbound (`Vais.Agents.Protocols.Mcp.Server`)

Hosting agents as MCP tools over stdio (Claude Desktop) + streamableHttp (web + ContextForge gateway). One MCP tool per registered agent id. Manifests published as `agent://{id}/{version}/manifest` resources.

- [x] **New guide**: `docs/guides/host-agents-as-mcp-tools.md` — stdio quick-start for Claude Desktop + streamableHttp quick-start for a web deployment. ~150 lines. Covers `McpAgentServerBuilder` + the "one tool per agent id" semantic + manifest-envelope JSON shape.
- [x] **Concept update**: `docs/concepts/interop.md` → add an "**Inbound MCP**" section after the existing "Outbound MCP" content. Symmetry matters; the doc currently reads as if MCP is outbound-only.
- [x] **Reference update**: `docs/reference/packages.md` → add `Vais.Agents.Protocols.Mcp.Server` (22 entries on public surface).
- [x] **Index update**: `docs/index.md` → add guide link.

### v0.8 — A2A inbound (`Vais.Agents.Protocols.A2A.Server` + `OrleansTaskStore`)

Hosts agents as A2A endpoints under `/agents/{id}` with auto-derived `AgentCard` at `.well-known/agent-card.json`. `OrleansTaskStore : A2A.ITaskStore` for durable `input-required` tasks.

- [x] **New guide**: `docs/guides/host-agents-as-a2a-endpoints.md` — ASP.NET Core host quick-start + interrupt → `Task(input-required)` → resume-via-taskId walkthrough + Orleans task-store wiring. ~180 lines.
- [x] **Concept update**: `docs/concepts/interop.md` → add an "**Inbound A2A**" section mirroring the MCP inbound section. Document the split-state story (A2A task = in-flight run; `taskId` is the resume key).
- [x] **Reference update**: `docs/reference/packages.md` → add `Vais.Agents.Protocols.A2A.Server`.
- [x] **Index update**: `docs/index.md` → add guide link.

### v0.9 — Graph orchestration (`IAgentGraph<TState>` + MAF Workflows adapter)

`InProcessGraphOrchestrator` (Pregel/BSP, zero-MAF-dep) + `Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework` adapter + `kind: AgentGraph` YAML/JSON loader in the shipped manifest packages + `OrleansCheckpointer` for durable interrupt → resume. K8s-style `{property, operator, value}` edge predicates with 10 operators + boolean combinators + `HandlerRef` escape hatch.

- [x] **New concept page**: `docs/concepts/graph-orchestration.md` — full walkthrough: `IAgentGraph<TState>` vs. `IAgentGraph` (JsonElement state bag); node / edge / predicate model; 10 operators; boolean combinators; `HandlerRef` escape; `InProcessGraphOrchestrator` vs. `MafGraphOrchestrator` trade-offs; interrupt / resume via `IResumableAgentGraph<TState>` + `OrleansCheckpointer`. ~350 lines.
- [x] **Concept update**: `docs/concepts/orchestration.md` → add a "graph orchestration" section + cross-link to the new concept page. Position graphs as the third orchestration style after Sequential / RoundRobin + Handoffs.
- [x] **New guide**: `docs/guides/compose-an-agent-graph-yaml.md` — author a `kind: AgentGraph` manifest (3 nodes, 2 edges with predicates), load it, invoke it in-process, inspect `AgentGraphEvent` stream. ~200 lines.
- [x] **New guide**: `docs/guides/run-resumable-graphs-on-orleans.md` — wire `OrleansCheckpointer` + interrupt-in-the-middle-of-a-graph + resume after silo restart. ~180 lines.
- [x] **New reference page**: `docs/reference/graph-predicate-operators.md` — table of 10 operators (real shipped names: `Eq`, `NotEq`, `Gt`, `Gte`, `Lt`, `Lte`, `Contains`, `NotContains`, `Exists`, `NotExists`) + `AllOf` / `AnyOf` / `Not` combinators + JSON shape examples.
- [x] **Reference update**: `docs/reference/packages.md` → add `Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework`.
- [x] **Reference update**: `docs/reference/events.md` → extend with the `AgentGraphEvent` subtypes (nine shipped: `GraphStarted`, `NodeStarted`, `NodeCompleted`, `EdgeTraversed`, `StateUpdated`, `GraphInterrupted`, `GraphResumed`, `GraphCompleted`, `GraphFailed`).
- [x] **Index update**: `docs/index.md` → add concept + guide + reference links.

### v0.10 — Streaming-filter pipeline + resilience (library layer)

Widened `IStreamingAgentFilter` with `InvokeAsync(request, next, ct) : IAsyncEnumerable<CompletionUpdate>` around-provider DIM. `StatefulAgentOptions.StreamingResiliencePipeline` sibling to the existing `ResiliencePipeline`. Per-turn retry boundary + Phase 2 drain on `StreamAsync`.

- [x] **Concept update**: `docs/concepts/execution-loop.md` → new "streaming filters" + "streaming resilience" subsections. Clarifies the shift from "streams bypass filters + Polly" (v0.4 behaviour documented here) to the v0.10 reality (three override points; Phase 1 retry; Phase 2 drain).
- [x] **Guide update**: `docs/guides/stream-with-tools.md` → extend with the new filter around-provider hook + resilience-pipeline wrapping. Show a typing-indicator filter + a retry-on-partial-stream-failure Polly setup.
- [x] **Reference update**: `docs/reference/events.md` → clarify that `CompletionDelta` (v0.12) is the streamed event that carries text; `StreamingAgentFilter.InvokeAsync` is upstream of the bus.
- [x] **New ADR**: `docs/adr/0003-streaming-filter-contract.md` — why one DIM method with three override points beats separate interfaces.
- [x] **Index update**: `docs/index.md` → note the streaming-filter extension under concepts/execution-loop.

### v0.11 — OpenAPI + Idempotency-Key (HTTP surface)

Built-in `Microsoft.AspNetCore.OpenApi 9.0.11` emission at `GET /openapi/v1.json` with `VaisProblemDetailsOperationTransformer` attaching `x-vais-type-urns` on error responses. Stripe-shape idempotency semantics — 24h TTL, SHA-256 body fingerprint, 4-tuple tenant-scoped keys, `Idempotency-Replayed` header, 422 mismatch, 409 + `Retry-After` in-flight. `IIdempotencyStore` + `InMemoryIdempotencyStore` + `OrleansIdempotencyStore`.

- [x] **New guide**: `docs/guides/enable-http-idempotency.md` — wiring `AddAgentControlPlaneIdempotency` (in-memory vs. Orleans backend) + client-side `Idempotency-Key` header convention + `Idempotency-Replayed` detection. ~150 lines.
- [x] **New guide**: `docs/guides/consume-the-openapi-spec.md` — where the spec lives, how the URN extension works, codegen pointers (NSwag / Kiota / openapi-typescript). ~120 lines.
- [x] **Concept update**: `docs/concepts/control-plane.md` → new "idempotency" + "OpenAPI" subsections. Contract-level; leave the wiring details for the guides.
- [x] **New reference page**: `docs/reference/problem-details-urns.md` — table of every `urn:vais-agents:*` URN the server emits, status code it pairs with, typical caller response. Covers v0.6 (manifest-invalid, policy-denied), v0.11 (idempotency-mismatch, idempotency-in-flight, agent-not-found, backend-unavailable, budget-exceeded, interrupt-pending), v0.12 (streaming-not-supported).
- [x] **Reference update**: `docs/reference/packages.md` → note `Microsoft.AspNetCore.OpenApi` + `System.Net.ServerSentEvents` dep-closure additions.
- [x] **Index update**: `docs/index.md` → add the two guides + the URN reference page.

### v0.12 — SSE streaming Invoke (HTTP surface)

`POST /v1/agents/{id}/invoke/stream` emits the full `AgentEvent` taxonomy as SSE (10 event kinds; `event:` field is the wire discriminator). New `CompletionDelta : AgentEvent` record + `IStreamingAiAgent` capability interface. Client gains `InvokeStreamAsync` (text) + `InvokeStreamEventsAsync` (full events) via `System.Net.ServerSentEvents` parser. Orleans streaming passthrough deferred (501 + URN).

- [x] **New guide**: `docs/guides/stream-invocations-over-http.md` — server-side route wiring + `StreamingInvokeOptions.HeartbeatInterval` + `StreamingEndpointAttribute` + client-side `await foreach` on `InvokeStreamEventsAsync` + cancellation via `HttpContext.RequestAborted`. ~200 lines.
- [x] **Concept update**: `docs/concepts/control-plane.md` → "streaming Invoke" subsection contrasting unary `POST /v1/agents/{id}/invoke` with SSE `POST /v1/agents/{id}/invoke/stream`. Document what travels on each event name.
- [x] **Concept update**: `docs/concepts/execution-loop.md` → add `IStreamingAiAgent` capability-interface reference + the `CompletionDelta` shape. Cross-link to control-plane.
- [x] **Reference update**: `docs/reference/events.md` → add `CompletionDelta` row + kebab-case SSE event name for every `AgentEvent` subtype (`turn.started`, `turn.completed`, `turn.failed`, `tool.started`, `tool.completed`, `tool.replayed`, `guardrail.triggered`, `interrupt.raised`, `handoff.requested`, `delta`).
- [x] **New ADR**: `docs/adr/0004-sse-event-taxonomy-on-wire.md` — why the full `AgentEvent` hierarchy rides the wire instead of text-only deltas.
- [x] **Index update**: `docs/index.md`.

### v0.13 — Kubernetes CRD + operator (`Vais.Agents.Control.KubernetesOperator`)

`Agent` CRD (`vais.io/v1alpha1`, namespaced, short names `vagent`/`vagents`). `AgentSpec` mirrors `AgentManifest` + `SecretRefs` + `PreserveOnDelete`. Reconcile via SHA-256 spec-hash diff + 3 conditions (`Ready` / `Synced` / `ManifestValid`) + 6-state phase + `ObservedGeneration` + `Idempotency-Key` from CR `{uid, generation, verb}`. KubeOps 10.3.4. Projected SA token + `DelegatingHandler` → runtime JWT. Helm chart at `deploy/helm/vais-agents-operator/`.

- [x] **New concept page**: `docs/concepts/kubernetes-operator.md` — how the operator fits into the control-plane stack; reconcile decision table; spec hash + observedGeneration pattern; conditions + phase enum; SA-token auth flow; secret-ref validation-only limitation. ~350 lines.
- [x] **New guide**: `docs/guides/deploy-the-kubernetes-operator.md` — docker-desktop quick-start: `docker build` → `helm install` → `kubectl apply Agent CR` → inspect status → delete. Mirrors the `deploy/README.md` intro but framed as a learning narrative. ~220 lines.
- [x] **New guide**: `docs/guides/wire-a-sidecar-opa-against-the-operator.md` — combined v0.13 + v0.14 deployment story — operator pod + OPA sidecar + ConfigMap-mounted Rego + Helm overlay. ~180 lines.
- [x] **New reference page**: `docs/reference/agent-crd.md` — CRD schema reference (fields, required vs. optional, defaults); status shape; finalizer name; printer columns; short names; annotation keys.
- [x] **Reference update**: `docs/reference/packages.md` → add `Vais.Agents.Control.KubernetesOperator` (library) + note the in-repo-only Host exe.
- [x] **Index update**: `docs/index.md` → add concept + 2 guides + reference.

### v0.14 — OPA policy engine (`Vais.Agents.Control.Policy.Opa`)

`OpaPolicyEngine : IAgentPolicyEngine` backed by sidecar HTTP (`POST /v1/data/{DataPath}`). Accepts both `bool` and object `{allowed, reason}` result shapes. `OpaInputBuilder` locked v1 schema (`schemaVersion`, `operation`, `principal`, `agent` full manifest). `DecisionCache` SHA-256-keyed 5s TTL with 1024-entry bound + 25%-oldest purge. FailMode=Closed default (deny on runtime error); 4xx → `InvalidOperationException` (adapter bug).

- [x] **New concept page**: `docs/concepts/opa-policy-engine.md` — why OPA over custom engines; the adapter's wire contract (including both result shapes); FailMode semantics; caching model; 4xx-is-a-bug vs. 5xx-is-a-policy-path separation. ~300 lines.
- [x] **Concept update**: `docs/concepts/control-plane.md` → "policy engines" subsection already mentions `IAgentPolicyEngine`; extend with a pointer to the OPA concept page + mention of when OPA is the right choice.
- [x] **New guide**: `docs/guides/gate-agents-with-opa.md` — end-to-end: run `opa` sidecar locally, write a tenant-scoped Rego policy, register `AddOpaPolicyEngine`, observe denials in the audit log. ~220 lines.
- [x] **New guide**: `docs/guides/author-a-rego-policy-against-the-vais-input-schema.md` — pattern-focused walkthrough: the 4 guard patterns (null principal, null agent, operation gate, multi-rule compose). Points at `samples/opa-policies/` for copy-paste starters. ~150 lines.
- [x] **Reference update**: `docs/reference/packages.md` → add `Vais.Agents.Control.Policy.Opa`.
- [x] **Cross-link**: `contracts/opa-input-schema.md` already exists — add a reciprocal "Rendered docs: [`opa-policy-engine.md`](../docs/concepts/opa-policy-engine.md)" at the top so readers land in the right place.
- [x] **Index update**: `docs/index.md` → add concept + 2 guides.

### v0.15 — CLI (`Vais.Agents.Cli`)

dotnet tool `vais`. 14 subcommands. `~/.vais/config.yaml` kubectl-shape. Token precedence `--token > VAIS_TOKEN > context user's token/tokenFile`. Exit codes POSIX 0/1/2/3/4/130. `apply -f` fallback via 409. `logs` = SSE attach.

- [x] **New getting-started page**: `docs/getting-started/install-the-cli.md` — `dotnet tool install -g Vais.Agents.Cli` + config-file bootstrapping (`vais config set-context local --server … --token …`) + verification (`vais version`, `vais get agents`). ~100 lines.
- [x] **New concept page**: `docs/concepts/cli.md` — subcommand map + config file shape + auth precedence + exit codes + `-o` output formats + `@file` argument convention. ~250 lines.
- [x] **New guide**: `docs/guides/apply-manifests-from-ci.md` — scripted `vais apply -f` over a CI pipeline: non-interactive delete via `--force`, exit-code handling in bash (`|| exit 2`), `@file` convention for large payloads. ~120 lines.
- [x] **New guide**: `docs/guides/tail-live-runs-with-vais-logs.md` — `vais logs <id> --only turn.completed,tool.completed` + `--since` client-side filter + Ctrl-C graceful shutdown. ~100 lines.
- [x] **New reference page**: `docs/reference/cli-subcommands.md` — per-command table: flags + arguments + exit codes + `-o` defaults. Thirteen commands total (9 root verbs + `config` branch with 4 sub-verbs) — plan's "14 subcommands" count drifted from shipped surface.
- [x] **New reference page**: `docs/reference/cli-config-file.md` — YAML schema for `~/.vais/config.yaml` + token-precedence chain + env-var overrides (`VAIS_CONFIG`, `VAIS_TOKEN`) + `vais config set-context` flag mapping.
- [x] **Reference update**: `docs/reference/packages.md` → add `Vais.Agents.Cli` dotnet tool with install command.
- [x] **Index update**: `docs/index.md` → add getting-started + concept + 2 guides + 2 reference pages.

---

## Sweeping updates (touch every existing doc)

- [x] **`docs/index.md`** — regenerate concept / guide / reference lists to include the 9 new concept pages + 13 new guides + 7 new reference pages landed per the pillar sections above. (Covered incrementally across PRs 1-6; PR 7 swept the stale "13 packages" text in the Architecture bullet.)
- [x] **`docs/getting-started/installation.md`** — bump package enumeration from 13 to 25 (24 library + 1 CLI dotnet-tool). Add the CLI install command. Link to `install-the-cli.md`. Scenario table rebuilt with per-pillar rows; dependency-pins list grew four entries (AspNetCore.OpenApi, ServerSentEvents, KubeOps, Spectre.Console.Cli).
- [x] **`docs/concepts/architecture.md`** — layered diagram regenerated: two Contracts nodes (Abstractions + Control.Abstractions), Protocols split into four (outbound + inbound), Orchestration subgraph for Graph.MAF, new Control-plane subgraph with seven packages, Tools subgraph for CLI. Per-layer prose refreshed to mention the new seams. "25 packages" count updated in the header.
- [x] **`docs/reference/packages.md`** — canonical list regenerated from the final 25 packages: Contracts (2) + Core + Adapters (2) + Hosting (2) + Persistence (3) + Observability (2) + Protocols outbound (2) + Protocols inbound (2) + Orchestration (1) + Control plane core (3 — InProcess + Manifests.Json + Manifests.Yaml) + Control plane HTTP (2) + Control plane Kubernetes (1) + Control plane Policy engine (1) + CLI (1). Added "Key entry points" column across all rows; scenario-bundles list refreshed; version pins block summarised at the bottom.
- [x] **`docs/reference/telemetry-keys.md`** — four new sections appended: "v0.11 — HTTP idempotency middleware" (4 keys + `Idempotency-Replayed` correlation note), "v0.12 — SSE streaming-invoke" (5 keys + `EmitPerEventSpans` sampling-opt-out), "v0.13 — Kubernetes operator" (5 keys + `KubeOps.Operator` meter pointer + correlation via `vais.correlation.id`), "v0.14 — OPA policy engine" (8 keys + deny-as-span-error convention). Constants block extended with the new `AgenticTags.*` families.
- [x] **`docs/reference/events.md`** — already swept in PR 2 (AgentGraphEvent hierarchy + SSE wire-event-name mapping) + PR 3 (CompletionDelta row in the Streaming section, ten-row wire-name table, heartbeat-comment note). No additional work needed this PR.
- [x] **`docs/adr/index.md`** — already updated in PR 3 (rows for 0003 + 0004). Verified current.

---

## Deliverable summary

Net-new pages: **21** (2 getting-started / 4 concepts / 9 guides / 5 reference / 2 ADR / 1 index refresh [counted as update not new]).

Updated pages: **8** (index / installation / architecture / packages / telemetry-keys / events / execution-loop / orchestration / control-plane / interop / adr-index).

Rough effort breakdown:
- **Concept pages** — 350-line each on average × 4 = ~1400 lines. 6-8h per page including type-audits + code snippets. **3-4 days**.
- **Guides** — 150-220 lines each × 13 = ~2300 lines. 3-4h per guide. **5-6 days**.
- **Reference pages** — 80-200 lines each × 5 = ~500 lines. 1-2h per page. **1-1.5 days**.
- **ADRs** — 100-150 lines each × 2 = ~250 lines. 2-3h per ADR (mostly retroactive documentation of decisions already made). **0.5 day**.
- **Sweeping updates** — ~1 day total.

**Total estimate: ~10-12 days focused writing work.** Sensible to split over 3 PRs (Concepts + index → Guides → Reference + ADRs + sweep). Or 1 PR per pillar, ~9 PRs, more natural to review.

---

## Proposed PR shape

**Option A — feature-axis (recommended):**
- **PR 1** — v0.7 + v0.8 inbound protocols docs (MCP + A2A): 2 guides + 1 concept update + 2 reference updates.
- **PR 2** — v0.9 graph orchestration: 1 new concept + 2 guides + 1 new reference + 2 reference updates.
- **PR 3** — v0.10 + v0.11 + v0.12 HTTP-surface docs: 3 new guides + 2 concept updates + 2 reference updates + 2 ADRs.
- **PR 4** — v0.13 Kubernetes: 1 new concept + 2 guides + 1 new reference.
- **PR 5** — v0.14 OPA policy: 1 new concept + 2 guides.
- **PR 6** — v0.15 CLI: 1 getting-started + 1 concept + 2 guides + 2 reference.
- **PR 7** — sweeping update + index regeneration + ADR index.

Each PR is 1-2 days. Review-friendly; readers land on one pillar at a time.

**Option B — layer-axis:**
- PR 1: All 4 new concept pages + index + architecture + packages updates.
- PR 2: All 13 new guides.
- PR 3: All 5 new reference pages + 3 reference updates + 2 ADRs.

Harder to review (broad surface per PR); faster overall if one author does it.

Lean = **Option A**.

---

## Non-goals

- **API-reference-by-package** (Sandcastle / DocFX auto-gen). PublicAPI.Shipped.txt is the source-of-truth; rendering as browseable docs is a separate DocFX-or-similar pillar.
- **Tutorial series**. The existing `getting-started/hello-agent.md` is the one tutorial; the new pages stay in concept / guide / reference slots.
- **Versioned docs site**. Everything lives on `main`; we version by git tag. A Hugo / Docusaurus site with per-version URLs is a release-polish concern.
- **Cookbook / recipes book**. Individual guides cover common recipes; a longer-form cookbook is out of scope.
- **Migration guides**. API is pre-alpha; no N-1 → N migration notes. If we break something during 0.x, the `Unshipped.txt` diff is the authoritative changelog.

---

## Progress log

- 2026-04-20 — plan created. Housekeeping inter-phase between pillar work. Covers the 9 post-v0.6 pillars shipped in this and the prior session. Package count went 14 (at v0.6) → 25 (at v0.15), and the current docs tree (12 concepts + 10 guides + 4 reference) was authored for the v0.6 state. Target deliverable: 21 net-new pages + 8 sweeping updates, ~10-12 days focused writing. Proposed PR shape = feature-axis, 7 PRs. **Pending**: start PR 1 (v0.7 + v0.8 inbound-protocols docs).

- 2026-04-20 — Docs PR 1 landed on `033-logging-improvement-read`. v0.7 + v0.8 inbound-protocol coverage. Two new guides: `docs/guides/host-agents-as-mcp-tools.md` (~130 lines — stdio + streamable-HTTP transports; `McpAgentServerOptions` shape; `LabelPrefixFilter`; JWT auth; three limitations listed) + `docs/guides/host-agents-as-a2a-endpoints.md` (~150 lines — ASP.NET Core host; `AgentCardBuilder` auto-derivation; three card-override hooks; `Task(input-required)` interrupt semantics; `OrleansTaskStore` durability; `A2AJwt` dedicated auth scheme). `docs/concepts/interop.md` restructured: landing paragraph now lists outbound + inbound symmetrically; two new major sections added — "MCP — inbound (agents as MCP tools)" + "A2A — inbound (agents as A2A endpoints)"; stale "both outbound-only" prologue removed; limitations-summary + see-also list reshaped to point at the four guides. `docs/reference/packages.md` header bumped from "13 packages / 0.4.0-preview" to "25 packages / 0.15.0-preview" with a per-pillar additions list; two new rows added under Protocols table (`Mcp.Server` + `A2A.Server`) with pillar version tags; typical-scenario bundles now lists "agents as MCP / A2A servers" as a composition. `docs/index.md` — interop bullet updated to mention inbound servers; two new guide entries added (labelled with version tags); packages-reference bullet bumped to 25; package-to-pillar quick-map now has two inbound rows. **Stats**: 2 net-new guides + 1 substantial concept rewrite + 2 reference updates + 1 index refresh. Zero code changes. **Pending**: Docs PR 2 (v0.9 graph orchestration — 1 new concept page + 2 new guides + 1 new reference page + 2 reference updates).

- 2026-04-20 — Docs PR 2 landed on `033-logging-improvement-read`. v0.9 graph-orchestration coverage. **One new concept page**: `docs/concepts/graph-orchestration.md` (~350 lines) — Pregel/BSP walkthrough; core-types table covering `IAgentGraph<TState>` vs shared-bag `IAgentGraph`, `AgentGraphManifest`, four node kinds (Agent / Code / Interrupt / End), ten predicate operators, four edge-effect kinds (`Set` / `Increment` / `Append` / `HandlerRef`), nine-subtype `AgentGraphEvent` hierarchy; `InProcessGraphOrchestrator` vs `MafGraphOrchestrator` trade-offs (the MAF adapter doesn't implement `IResumableAgentGraph<TState>` in v0.9); checkpoint/resume semantics + `InMemoryCheckpointer` vs `OrleansCheckpointer` split; full YAML example of a 3-node `support-triage` graph in the v0.6 envelope shape; choose-your-orchestrator decision table against v0.4 Sequential/RoundRobin/Handoff; five-item v0.9-limitations list. **Two new guides**: `docs/guides/compose-an-agent-graph-yaml.md` (~195 lines — v0.6 envelope shape; 3-node triage manifest; agent registration; `YamlAgentGraphManifestLoader` load-time validation error contract; in-process invoke with the shared-bag `IAgentGraph`; full `AgentGraphEvent` stream rendering + sample output) + `docs/guides/run-resumable-graphs-on-orleans.md` (~170 lines — approval-gated triage with an `Interrupt` node; `AddOrleansGraphCheckpointer` ordering caveat; `/triage` + `/triage/{runId}/approve` endpoint pair; cross-silo-restart event-stream rendering; retention + cleanup guidance; testing with `InMemoryCheckpointer`). **One new reference page**: `docs/reference/graph-predicate-operators.md` (~185 lines — subtype summary + ten-operator table with type-compat rules; dotted-property-path conventions including `resume.payload.*` + `lastMessage.*`; YAML/JSON shape examples for every subtype; `HandlerRef` escape with `predicateResolver` wiring example; validation-at-load rules; .NET API shape block). **Two reference updates**: `docs/reference/events.md` grew a dedicated `AgentGraphEvent` section — base ctor; subtypes-table with SSE wire names; constructor-position cheatsheet. `docs/reference/packages.md` grew a new **Orchestration (multi-agent)** section with `Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework`; typical-scenario-bundles list gained a "Graph orchestration" row. **One concept update**: `docs/concepts/orchestration.md` grew a "Graph orchestration (v0.9)" section with the sibling-not-replacement positioning + a four-row decision table that picks between Sequential / RoundRobin / `Handoff` / `IAgentGraph`; stale v0.4 "deferred `IAgentGraphExecutor`" limitation removed. **One index refresh**: `docs/index.md` gained a Concepts link (graph orchestration), two Guides links (compose + resumable-on-Orleans), a Reference link (graph-predicate-operators), and two new quick-map rows. **Plan corrections in passing**: the pillar-plan's operator names (`Equals` / `NotEquals` / `In` / `NotIn` / `GreaterThan` / …) didn't match the shipped enum (`Eq` / `NotEq` / `Gt` / `Gte` / `Lt` / `Lte` / `Contains` / `NotContains` / `Exists` / `NotExists`) — reference page uses the real names and plan-doc checkboxes annotate the drift. The `EdgeEvaluated` event name in the plan was also wrong (shipped as `EdgeTraversed`); same for the missing-from-the-plan `StateUpdated` / `GraphResumed` / `GraphFailed` subtypes. Plus a drive-by fix to the concept-page YAML example to use the actual v0.6 envelope shape (`metadata` + `spec` split). **Stats**: 3 net-new pages (1 concept + 2 guides) + 1 net-new reference + 2 reference updates + 1 concept update + 1 index refresh. Zero code changes.

- 2026-04-20 — Docs PR 3 landed on `033-logging-improvement-read`. v0.10 + v0.11 + v0.12 HTTP-surface coverage. **Three new guides**: `docs/guides/enable-http-idempotency.md` (~170 lines — `AddAgentControlPlaneIdempotency` wiring, `IdempotencyOptions` defaults, `OrleansIdempotencyStore` ordering caveat, client-side key convention, `Idempotency-Replayed` detection, 400/409/422 error matrix, 4-tuple scoping, body fingerprinting, testing with `FakeTimeProvider`), `docs/guides/consume-the-openapi-spec.md` (~140 lines — `/openapi/v1.json` route, `x-vais-type-urns` extension, NSwag + Kiota + openapi-typescript recipes, regeneration strategies, `VaisProblemDetailsOperationTransformer` internals, custom-URN extension hook), `docs/guides/stream-invocations-over-http.md` (~215 lines — `StreamingInvokeOptions.HeartbeatInterval`, `[StreamingEndpoint]` idempotency bypass, `IStreamingAiAgent` capability probe, client's `InvokeStreamAsync` vs `InvokeStreamEventsAsync` shapes, full wire-event table, raw SSE example, cancellation via `HttpContext.RequestAborted`, three limitations). **Two new ADRs**: `docs/adr/0003-streaming-filter-contract.md` — why one DIM method with three override points beats separate interfaces + Polly Phase 1/Phase 2 rules, and `docs/adr/0004-sse-event-taxonomy-on-wire.md` — why the full `AgentEvent` hierarchy rides the wire instead of text-only deltas. **One new reference page**: `docs/reference/problem-details-urns.md` — ten-URN table covering every status code the server emits + typical-caller-response column + namespace-discipline note for host-specific URNs. **Concept updates**: `docs/concepts/execution-loop.md` grew "Streaming filters (v0.10)", "Streaming resilience (v0.10)", and "Streaming Invoke over HTTP (v0.12)" sections; stale "filters + resilience bypassed on StreamAsync" limitation removed; extension points + see-also lists refreshed. `docs/concepts/control-plane.md` grew "Idempotency (v0.11)", "OpenAPI (v0.11)", "Streaming Invoke (v0.12)" subsections with the unary-vs-streaming decision table; stale v0.4 "no HTTP API / no lifecycle-manager impl" limitations removed. **Guide updates**: `docs/guides/stream-with-tools.md` replaced the v0.4.1 "what's bypassed on streaming" section with full "Streaming filters (v0.10)" + "Streaming resilience (v0.10)" treatment — two example filters (`TypingIndicatorFilter` around-provider, `PiiScrubFilter` per-delta) + the Phase 1/Phase 2 Polly rule. **Reference updates**: `docs/reference/events.md` grew a Streaming (v0.12) row for `CompletionDelta`, added it to the constructor-positions cheatsheet, and appended a ten-row "SSE wire-event names (v0.12)" table with the heartbeat-comment note. `docs/reference/packages.md` grew a new "Control plane HTTP (server + client)" section with `Control.Http.Server` + `Control.Http.Client` rows (noting the `Microsoft.AspNetCore.OpenApi 9.0.11` + `System.Net.ServerSentEvents 10.0.2` transitive deps); typical-scenario-bundles list gained two v0.11/v0.12 rows. **ADR index**: two new rows for 0003 + 0004. **Index refresh**: three new guide entries, one new reference entry, two new quick-map rows. **Stats**: 3 net-new guides + 2 net-new ADRs + 1 net-new reference + 3 concept/guide updates (control-plane, execution-loop, stream-with-tools) + 2 reference updates (events, packages) + ADR index + main index refresh. Zero code changes.

- 2026-04-20 — Docs PR 4 landed on `033-logging-improvement-read`. v0.13 Kubernetes-operator coverage. **One new concept page**: `docs/concepts/kubernetes-operator.md` (~320 lines) — topology diagram showing the operator-as-HTTP-adapter pattern; CRD metadata table (apiGroup `vais.io`, version `v1alpha1`, short names `vagent`/`vagents`); reconcile loop's four-step decision algorithm (validate → hash → decide verb → status write); `SpecHasher.Compute` canonical-JSON SHA-256 rules; `ObservedGeneration` freshness convention; six-state `AgentPhase` state machine with transition diagram; three-condition matrix (`Ready` / `Synced` / `ManifestValid` × Active/Error/Terminating); `Idempotency-Key = {uid}:{gen}:{verb}` derivation; projected SA token + `ServiceAccountTokenHandler` flow; Helm chart values overview; four v0.13 limitations (secret-refs validation-only, HPA not wired, single-replica/no-leader-election, `x-kubernetes-preserve-unknown-fields` on opaque sub-schemas). **Two new guides**: `docs/guides/deploy-the-kubernetes-operator.md` (~240 lines — Docker Desktop quick-start; operator image build; local control-plane bootstrap on `host.docker.internal:5080`; `helm install` with key values; CR apply + lifecycle watch; `kubectl describe` status walkthrough; spec-edit update path; `preserveOnDelete` finalizer path; teardown; five common pitfalls with resolutions) + `docs/guides/wire-a-sidecar-opa-against-the-operator.md` (~235 lines — combined v0.13 + v0.14 deployment; pod-topology diagram; tenant-scoped Rego sample; ConfigMap creation; two overlay options (`kubectl patch` vs chart fork); sidecar verification commands; `AddOpaPolicyEngine` wiring on control plane; policy-decision trigger walkthrough; hot-reload via `kubectl rollout restart`; four operational notes on fail-mode, decision cache, 4xx semantics, wire bytes). **One new reference page**: `docs/reference/agent-crd.md` (~220 lines — CRD metadata table; five printer columns with JSON paths; spec schema split into six required + 18 optional fields with types + behaviour notes; secret-refs sub-shape explainer with v0.13 validation-only caveat; status schema table; six-value phase enum; condition-shape YAML example; seven-reason vocabulary table mapping reason codes to firing conditions + paired condition statuses; full CR example + full status example). **Reference update**: `docs/reference/packages.md` grew a new "Control plane — Kubernetes (v0.13)" section with the `Vais.Agents.Control.KubernetesOperator` row + an out-of-table note calling out the in-repo-only Host exe + Helm chart; typical-scenario-bundles list gained a "Kubernetes-native deployment" entry. **Index refresh**: Concepts link for kubernetes-operator; two Guides links (deploy, wire-OPA-sidecar); Reference link (agent-crd); quick-map row for "Deploy agents declaratively from Kubernetes". **Stats**: 1 net-new concept + 2 net-new guides + 1 net-new reference + 1 reference update (packages) + 1 index refresh. Zero code changes.

- 2026-04-20 — Docs PR 5 landed on `033-logging-improvement-read`. v0.14 OPA-policy-engine coverage. **One new concept page**: `docs/concepts/opa-policy-engine.md` (~310 lines) — "why OPA over a custom engine" four-point rationale; wire-contract walkthrough with both bool + object response shapes; v1 input schema overview table (`schemaVersion`/`operation`/`principal`/`agent`) cross-linked to `contracts/opa-input-schema.md`; schema-evolution rule summary; `OpaFailMode.Closed` vs `Open` decision table with prod/dev posture guidance; the "4xx is a bug, 5xx is a policy path" error-classification rule with a four-row response-handling matrix; decision-cache semantics (SHA-256 key, 5s TTL, 1024-entry bound, 25% eldest purge, `TimeSpan.Zero` to disable); singleton DI wiring rationale; the `Vais.Agents.Policy.OPA` observability activity with eight tagged dimensions; three "when OPA is the right choice" vs "when it isn't" bullets; four v0.14 limitations (policy-version logging, decision-cache overlap with hot-reload, no-Rego-distribution, no-OPA-decision-log cross-correlation). **Two new guides**: `docs/guides/gate-agents-with-opa.md` (~230 lines — ten-step walkthrough: docker-run OPA, write + PUT Rego, three sanity-check curl invocations, C# host wiring, cross-tenant deny → `403 urn:vais-agents:policy-denied` with reason flow, OTel audit-trail rendering with eight tag readouts, dev-vs-prod `FailMode` flip, intentionally-broken `DataPath` to trigger the 4xx-bug path, teardown, four known pitfalls) + `docs/guides/author-a-rego-policy-against-the-vais-input-schema.md` (~210 lines — the four canonical guard patterns: null-principal, null-agent, operation-gate, multi-rule-compose; each with a working Rego snippet + "why this matters" rationale; local-testing recipe with `opa eval` + `opa test` sibling-file convention; five common mistakes + callouts). **Concept update**: `docs/concepts/control-plane.md` grew a new "Policy engines" subsection that positions `AllowAllPolicyEngine` / `OpaPolicyEngine` / custom engines and lists three "when OPA is right" vs two "when it isn't" bullets, cross-linking to the new concept page. **Reference update**: `docs/reference/packages.md` grew a new "Control plane — Policy engine (v0.14)" section with the `Vais.Agents.Control.Policy.Opa` row noting the v1 input schema + decision cache + FailMode.Closed default; typical-scenario-bundles list gained an "OPA-gated control plane" entry. **Contracts cross-link**: `contracts/opa-input-schema.md` gained a reciprocal "Rendered docs:" block at the top pointing at the concept page + the guard-pattern guide. **Index refresh**: Concepts link for opa-policy-engine; two Guides links (gate-agents-with-opa, author-a-rego-policy); quick-map row for "Gate every agent verb through a Rego policy". **Stats**: 1 net-new concept + 2 net-new guides + 2 concept/reference updates (control-plane, packages) + 1 contracts cross-link + 1 index refresh. Zero code changes.

- 2026-04-20 — Docs PR 6 landed on `033-logging-improvement-read`. v0.15 CLI coverage. **One new getting-started page**: `docs/getting-started/install-the-cli.md` (~140 lines) — `dotnet tool install -g Vais.Agents.Cli` + tool-command-on-PATH explainer; `vais config set-context` first-run walkthrough; multi-context switching; `vais get agents` + `vais apply -f` + `vais invoke` smoke test; four-row token-precedence table; env-var table; six-row exit-code table with a bash `case` CI pattern. **One new concept page**: `docs/concepts/cli.md` (~240 lines) — nine-row top-level subcommand map with HTTP-verb column; four-row `config` branch table; kubeconfig-shape file overview; four-step token-precedence chain; POSIX exit-code semantics; `-o` output-format matrix per command; `@file` convention; `apply -f` create-or-update logic (including 409 fallback); `vais logs` SSE attach overview; "when to use the CLI vs `AgentControlPlaneClient`" decision; four v0.15 limitations (no dry-run, no paging, no shell completion, no diff view). **Two new guides**: `docs/guides/apply-manifests-from-ci.md` (~180 lines — GitHub Actions skeleton; idempotency-key rotation pattern; six-branch exit-code `case` template; non-interactive `--force` delete; `@file` for large payloads; `--context` per-call for multi-env pipelines; three token-management patterns; five common issues) + `docs/guides/tail-live-runs-with-vais-logs.md` (~150 lines — basic tail rendering; `--only` filter with ten-row event-kind table; `--since` client-side time-bounded tail; Ctrl-C graceful-shutdown semantics; four useful pipeline recipes; combining with `invoke --stream`; four error paths; CI smoke-test snippet). **Two new reference pages**: `docs/reference/cli-subcommands.md` (~180 lines — one section per command: `version` / `init` / `get` / `apply` / `delete` / `cancel` / `invoke` / `logs` / `signal` + 4 `config` sub-verbs, each with full arguments/flags/exit-codes table; dedicated section for exit codes, `@file` convention, and output-format defaults) + `docs/reference/cli-config-file.md` (~170 lines — top-level schema; per-record breakdown of `clusters[]` / `users[]` / `contexts[]` / `currentContext`; full example; env-var reference; four-step token-precedence chain; `set-context` flag-to-field mapping table; migration / sharing patterns). **Reference update**: `docs/reference/packages.md` grew a new "CLI (v0.15)" section with the `Vais.Agents.Cli` row + an explicit NU1212 caveat; typical-scenario-bundles list gained a "CLI over the HTTP control plane" entry. **Index refresh**: Getting-started link for install-the-cli; Concepts link for CLI; two Guides links (apply-from-ci, tail-logs); two Reference links (subcommands, config-file); quick-map row for "Operate the control plane from a shell". **Plan corrections in passing**: the pillar-plan's "14 subcommands" count didn't match the shipped surface — CLI ships 13 targets (9 root verbs + `config` branch with 4 sub-verbs). Reference page annotates the drift. **Stats**: 1 net-new getting-started + 1 net-new concept + 2 net-new guides + 2 net-new references + 1 reference update (packages) + 1 index refresh. Zero code changes.

- 2026-04-20 — Docs PR 7 landed on `033-logging-improvement-read`. Sweeping-updates pass — every doc that carried a "13 packages" or "v0.6" shape gets brought up to the v0.15 state. **`docs/getting-started/installation.md`** rewritten: header count 13 → 25; added a "Install the CLI" section with `dotnet tool install -g Vais.Agents.Cli`; scenario table rebuilt with nine new rows covering v0.7 + v0.8 inbound protocols, v0.9 graph orchestration (including the MAF-adapter variant), v0.11 HTTP control plane, v0.12 streaming client, v0.13 Kubernetes operator, v0.14 OPA policy engine, and v0.15 CLI; dependency-pins list grew `Microsoft.AspNetCore.OpenApi 9.0.11`, `System.Net.ServerSentEvents 10.0.2`, `KubeOps 10.3.4`, `Spectre.Console.Cli 0.55.0`; verify-your-install block gained a one-line `vais version` variant. **`docs/concepts/architecture.md`** regenerated: Mermaid diagram now renders 25 nodes across ten subgraphs (Contracts with 2 nodes including the new `Control.Abstractions`, Core, Adapters, Hosting, Persistence, Observability, Protocols with 4 nodes covering inbound + outbound, Orchestration subgraph for `Graph.MAF`, Control plane subgraph with 7 nodes, Tools subgraph for CLI); 25 edges show the full dependency graph including `CMY → CMJ`, `CHS → CIP + CMJ`, `CKO → CA + CHC`, `CLI → CHC + CMY`; prose sections added/updated for Abstractions (two contract layers), Protocols (outbound vs inbound split), Orchestration (three-style overview), Control plane (seven-package walkthrough), Observability (new tag families pointer). **`docs/reference/packages.md`** fully regenerated as the canonical list: new "Key entry points" column across all 25 rows; proper v0.6+ ordering (Contracts → Core → Adapters → Hosting → Persistence → Observability → Protocols outbound → Protocols inbound → Orchestration → Control plane core → Control plane HTTP → Control plane Kubernetes → Control plane Policy engine → CLI); four missing rows added (`Control.Abstractions`, `Control.InProcess`, `Control.Manifests.Json`, `Control.Manifests.Yaml`) — surface now actually sums to 25; scenario-bundles list updated with v0.6-through-v0.15 bundles + the `Control.Manifests.Yaml` pairing on the graph-orchestration row; version-pins block at bottom summarises the 12 transitive dep-closure pins. **`docs/reference/telemetry-keys.md`** grew four new pillar-tagged sections: v0.11 idempotency-middleware keys (4 tags: key, status, fingerprint, store), v0.12 SSE streaming keys (5 tags: session, event-count, heartbeat-count, closed-reason, event-kind; per-event-span opt-out note), v0.13 Kubernetes-operator keys (5 tags: crd-version, phase, verb, manifest-revision, observed-generation; cross-reference to KubeOps.Operator meter), v0.14 OPA policy-engine keys (8 tags: operation, agent.id, agent.version, principal.tenant, cache-hit, decision, deny-reason, opa.status-code; deny-as-span-error convention); constants-in-code block extended to mention the new `AgenticTags.{Control,Stream,Operator,Policy}.*` families. **`docs/index.md`** final sweep: the stale "13 packages" in the Architecture bullet was bumped to "25 packages" (everything else already correct across PRs 1-6). **`docs/reference/events.md`** + **`docs/adr/index.md`** — verified current; the v0.9 + v0.12 sweeps landed in PR 2 + PR 3 respectively, and ADR index got 0003 + 0004 rows in PR 3. **Stats**: 4 full page rewrites (installation, architecture, packages, telemetry-keys) + 1 small index fix. Zero code changes. **Pending**: PR 4 (v0.13 Kubernetes) through PR 7 are all landed on this branch; the housekeeping-docs inter-phase is complete. Next inter-phase work per the separate housekeeping plan: the samples plan (`plans/actor-agents-oss-housekeeping-samples-plan.md`) — 15 net-new runnable samples, 8 PRs, ~9-10 days focused implementation.
