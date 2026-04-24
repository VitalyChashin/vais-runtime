# Housekeeping — samples update plan (v0.7 → v0.15)

Scope: bring `oss/agentic/samples/` to parity with the nine pillars that landed after v0.6. Inter-phase housekeeping — no new library code, just new sample projects and a version-bump sweep over the existing 21. Companion to [`actor-agents-oss-housekeeping-docs-plan.md`](./actor-agents-oss-housekeeping-docs-plan.md). Created 2026-04-20.

---

## Current state

**Samples tree as of v0.15.0-preview** (`oss/agentic/samples/`):

| Runnable | Count | Status |
|---|---|---|
| Live (net9.0 consoles) | 21 | Pinned at **`0.4.0-preview`** (11 releases stale) |
| Doc-only (README + Rego/Helm) | 2 | `opa-policies/`, `opa-sidecar/` — shipped with v0.14 |

The 21 live samples cover the v0.1 – v0.4 surface (library-layer pillars) + the v0.5 Orleans persistence story. Nothing exercises the v0.7 – v0.15 HTTP / inbound / graph / operator / policy / CLI surfaces from a runnable console.

**Coverage gap (runnable samples)**:
- v0.7 MCP inbound (`Vais.Agents.Protocols.Mcp.Server`) — **no sample**
- v0.8 A2A inbound (`Vais.Agents.Protocols.A2A.Server` + `OrleansTaskStore`) — **no sample**
- v0.9 Graph orchestration (`IAgentGraph<T>` / `InProcessGraphOrchestrator` / MAF Workflows adapter / `kind: AgentGraph`) — **no sample**
- v0.10 Streaming filter + resilience pipeline — **no sample** (`HelloStreamingTools` pre-dates the around-provider hook)
- v0.11 OpenAPI + Idempotency-Key HTTP surface — **no sample**
- v0.12 SSE streaming Invoke on HTTP surface — **no sample** (`HelloStreaming` covers library-layer only)
- v0.13 Kubernetes operator — **no sample** (Helm chart + Dockerfile ship under `deploy/`, not runnable from `samples/`)
- v0.14 OPA policy engine — Rego samples exist but **no runnable C# sample** wiring `AddOpaPolicyEngine`
- v0.15 CLI — the CLI itself is the sample; **no runnable scripted walkthrough** showing sequences / recipes

**Version-bump gap**: every existing sample csproj pins `0.4.0-preview`. Consumers building from the current feed get a nuget-resolution fail.

---

## Principles

- **One feature = one sample**. Crisp scope. Don't bundle two pillars into one sample unless they're already coupled (e.g., v0.13 + v0.14 sidecar pattern).
- **Runnable in ≤ 5 seconds** (cold-build excluded). Scripted fake completion providers > live LLM where possible. Live-LLM samples gate on `OPENAI_API_KEY`.
- **No Docker unless required**. v0.7 stdio MCP doesn't; v0.8 A2A HTTP host doesn't; v0.11 + v0.12 need an in-process `WebApplication` but no container; only v0.13 operator demo and v0.14 OPA-integration demo need Docker (kind / OPA sidecar).
- **Short, annotated `Program.cs`**. Target ~80-150 lines. Comments inline explain the non-obvious call.
- **Every sample has a README**. Structure: one-paragraph intro → run command → expected output → what it demonstrates → pointer to the matching concept / guide doc.
- **Consistent dependency pattern**: all samples reference `0.15.0-preview` packages via `NuGet.config` pointing at `artifacts/packages/`. No exceptions.
- **Deterministic where possible**. Scripted event streams, static `FakeCompletionProvider`, hardcoded responses — same pattern as `HelloStreaming` / `ToolGuardrailsAndInterrupt` today. Keeps CI cost zero.
- **Cross-link docs → samples → docs**. Docs plan adds a "sample" column; this plan adds the "docs" column in each new sample README.

---

## Version-bump sweep (all 21 existing samples)

Mechanical: `0.4.0-preview` → `0.15.0-preview` across every csproj. Plus a pass-through to verify each still runs.

- [ ] Update csprojs:
  - HelloAgent / PromptComposer / CustomMemoryStore / ContextProviderRag / InputOutputGuardrails / ToolGuardrailsAndInterrupt / BudgetEnforcement / ToolFromFunc / AgentManifestAndRegistry / HelloStreaming / HelloStreamingTools / SequentialOrchestration / RoundRobinOrchestration / HandoffBetweenAgents / OrleansSilo / OrleansRedisPersistence / OrleansPostgresPersistence / ObservabilityOtelConsole / VectorDataRag / McpToolSourceExample / A2ARemoteAgentExample
- [ ] Verify each sample still builds + runs against the local `0.15.0-preview` feed. Note any that broke on an intermediate milestone; fix forward.
- [ ] Update `build-all.sh` + `build-all.ps1` if they carry hardcoded version refs.
- [ ] Refresh any expected-output snippets in READMEs that changed between v0.4 and v0.15 (e.g., `SampleManifestAndRegistry` printing `AgentManifest` shape — manifest gained ~12 new optional fields in v0.6).

Estimate: **~0.5 day** (grep + sed + rebuild loop; small fix-forward per broken sample).

---

## Pillar-by-pillar new-sample map

### v0.7 — MCP inbound

**New sample: `McpServerStdio`** — 1 runnable console.
- Boots an `McpAgentServerBuilder` over stdio. Exposes one `StatefulAiAgent` as an MCP tool. A scripted client feed simulates Claude Desktop → tool-call → response round-trip.
- Packages: `Vais.Agents.Abstractions`, `Vais.Agents.Core`, `Vais.Agents.Hosting.InMemory`, `Vais.Agents.Protocols.Mcp.Server`, `ModelContextProtocol`.
- ~150 LoC. Deterministic (no API key).
- README: *"Host an agent as an MCP tool over stdio — Claude Desktop can consume it."*
- Cross-link: `docs/guides/host-agents-as-mcp-tools.md`.

**New sample: `McpServerHttp`** — 1 runnable console.
- ASP.NET Core `WebApplication` hosting `McpAgentServer` over streamableHttp at `POST /mcp`. A co-located client in the same process POSTs one tool-call and prints the response. Manifests published as `agent://{id}/v1/manifest` resources.
- Packages: above + `Microsoft.AspNetCore.App` framework ref.
- ~180 LoC. Deterministic.
- README: *"Host an agent as an MCP tool over streamableHttp — web deployments + ContextForge-gateway composition."*

### v0.8 — A2A inbound

**New sample: `A2AServerBasics`** — 1 runnable console.
- ASP.NET Core `WebApplication` hosting `A2AAgentServerBuilder` under `/agents/{id}`; auto-derives `AgentCard` at `.well-known/agent-card.json`. A co-located in-process client posts a message and prints the result.
- Packages: `Vais.Agents.Abstractions`, `Vais.Agents.Core`, `Vais.Agents.Hosting.InMemory`, `Vais.Agents.Protocols.A2A.Server`, `A2A`, `Microsoft.AspNetCore.App`.
- ~170 LoC.
- README: *"Host an agent as an A2A endpoint — `.well-known/agent-card.json` auto-derived."*

**New sample: `A2AInterruptResumeOrleans`** — 1 runnable console.
- Wires `OrleansTaskStore : A2A.ITaskStore` on a single-process Orleans silo. Runs an agent that emits an `InterruptRaised` → A2A reports `Task(input-required)` → client resumes via `taskId`. Demonstrates durable HITL through silo restart (simulated in-process).
- Packages: above + `Vais.Agents.Hosting.Orleans` + Orleans test host.
- ~220 LoC. No external Docker; uses Orleans' in-memory TestCluster.
- README: *"Interrupt → durable A2A task → resume across simulated silo restart."*

### v0.9 — Graph orchestration

**New sample: `AgentGraphInProcess`** — 1 runnable console.
- Builds an `IAgentGraph<GraphState>` in code using `InProcessGraphOrchestrator`. 3 nodes (classifier → branch A / branch B), 2 edges with `K8sLikePredicate` (e.g., `{property: "category", operator: "Equals", value: "support"}`). Prints the `AgentGraphEvent` stream.
- Packages: `Vais.Agents.Abstractions`, `Vais.Agents.Core`.
- ~180 LoC. Deterministic.
- README: *"Compose a 3-node branching graph in-process. No YAML; plain C# `IAgentGraphBuilder` call chain."*
- Cross-link: `docs/concepts/graph-orchestration.md` + `docs/reference/graph-predicate-operators.md`.

**New sample: `AgentGraphYamlLoader`** — 1 runnable console.
- Ships a `graph.yaml` with `kind: AgentGraph` + 4 nodes + predicates + `HandlerRef` escape for one node. Loads via `YamlAgentGraphManifestLoader`, runs, prints events. Same shape as `AgentGraphInProcess` but manifest-driven.
- Packages: above + `Vais.Agents.Control.Manifests.Yaml`.
- ~120 LoC. Deterministic.
- README: *"Load a graph from `graph.yaml` with predicate edges and a `HandlerRef` escape."*

**New sample: `AgentGraphMaf`** — 1 runnable console.
- Same graph shape as `AgentGraphInProcess`, but uses `MafGraphOrchestrator` (from `Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework`). Side-by-side output proves parity.
- Packages: above + `Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework`, `Microsoft.Agents.AI.Workflows`.
- ~160 LoC.
- README: *"Run the same graph shape on MAF Workflows — prove cross-stack parity."*

**New sample: `AgentGraphResumeOnOrleans`** — 1 runnable console.
- Graph with a node that raises `GraphInterrupted`; `OrleansCheckpointer` persists state to Redis (in-memory TestCluster with `Microsoft.Orleans.Persistence.Memory`); new process loads the checkpoint and resumes the graph from the interrupted node.
- Packages: above + `Vais.Agents.Hosting.Orleans`.
- ~220 LoC. No external Docker.
- README: *"Interrupt → checkpoint → resume a graph across simulated process boundaries."*

### v0.10 — Streaming filter + resilience

**New sample: `StreamingFilterTypingIndicator`** — 1 runnable console.
- Implements `IStreamingAgentFilter.InvokeAsync` around-provider hook that prints `.`s while the provider produces deltas, plus `OnStreamDeltaAsync` that throttles printing. Scripted completion provider emits 20 deltas with sleeps. Exit shows clean assistant-text.
- Packages: `Vais.Agents.Abstractions`, `Vais.Agents.Core`.
- ~110 LoC. Deterministic.
- README: *"Wrap the streaming turn with a typing-indicator filter."*

**New sample: `StreamingResiliencePolly`** — 1 runnable console.
- `StatefulAgentOptions.StreamingResiliencePipeline` with a 3-try retry policy on `TransientProviderException`. Scripted provider throws on first 2 attempts; 3rd succeeds. Output proves Phase-1 retry + Phase-2 drain behaviour.
- Packages: above + `Microsoft.Extensions.Resilience`.
- ~130 LoC. Deterministic.
- README: *"Add Polly-backed resilience to `StreamAsync` without breaking already-yielded deltas."*

### v0.11 — OpenAPI + Idempotency-Key

**New sample: `HttpIdempotencyInMemory`** — 1 runnable console.
- In-process ASP.NET Core `WebApplication` + `AddAgentControlPlane().AddAgentControlPlaneIdempotency(...)`. Sends two identical `POST /v1/agents/{id}/invoke` requests with the same `Idempotency-Key` header; verifies the second returns `Idempotency-Replayed: true` with a cached body.
- Packages: `Vais.Agents.Abstractions`, `Vais.Agents.Core`, `Vais.Agents.Control.Abstractions`, `Vais.Agents.Control.InProcess`, `Vais.Agents.Control.Http.Server`, `Vais.Agents.Control.Http.Client`, `Vais.Agents.Hosting.InMemory`.
- ~200 LoC. Deterministic.
- README: *"Wire the v0.11 idempotency middleware, retry the same call, see `Idempotency-Replayed: true`."*

**New sample: `OpenApiSpecExplorer`** — 1 runnable console.
- Boots the same `WebApplication` as above, fetches `/openapi/v1.json`, parses it, prints the paths + the `x-vais-type-urns` extension for every error response.
- Packages: above + `Microsoft.AspNetCore.OpenApi`.
- ~120 LoC. Deterministic.
- README: *"Inspect the shipped OpenAPI spec + its VAIS URN extension."*

### v0.12 — SSE streaming Invoke on HTTP

**New sample: `HttpStreamingInvoke`** — 1 runnable console.
- In-process server with `MapAgentControlPlane()` including `POST /v1/agents/{id}/invoke/stream`. Client consumes the SSE stream via `AgentControlPlaneClient.InvokeStreamEventsAsync` and prints each `AgentEvent` with colour. Shows all 10 event kinds.
- Packages: same set as `HttpIdempotencyInMemory`.
- ~220 LoC. Scripted `IStreamingCompletionProvider` guarantees the 10 event kinds fire.
- README: *"End-to-end SSE streaming over the HTTP control plane."*
- Cross-link: `docs/guides/stream-invocations-over-http.md`.

**New sample: `HttpStreamingCancellation`** — 1 runnable console.
- Spawns a slow-yielding provider (~10ms between deltas, 50 deltas total). Client calls `InvokeStreamEventsAsync` with a `CancellationTokenSource` that fires after 100ms. Prints received events + proves the server stops mid-turn on `RequestAborted`.
- Packages: same set.
- ~170 LoC.
- README: *"Cancel an in-flight SSE stream cleanly — demonstrates Ctrl-C semantics."*

### v0.13 — Kubernetes operator

**Doc-only sample: `KubernetesOperatorQuickstart`** — directory with README + a `sample-agent.yaml`.
- No runnable console (already covered by `deploy/helm/vais-agents-operator/` + `src/Vais.Agents.Control.KubernetesOperator.Host`). Adds a `samples/KubernetesOperatorQuickstart/README.md` that scripts the docker-desktop happy path: `docker build` → `helm install` → `kubectl apply` → observe → delete.
- Ships `sample-agent.yaml` under the dir for copy-paste convenience.
- Cross-link: `docs/guides/deploy-the-kubernetes-operator.md`.

*Rationale: the operator needs a real K8s cluster; no in-process simulation equivalent. Keep the runnable-sample bar honest and route users to the deploy tree.*

### v0.14 — OPA policy engine

**New sample: `OpaPolicyGateLocal`** — 1 runnable console.
- Boots an in-process ASP.NET Core `WebApplication` with the v0.6 control plane + `AddOpaPolicyEngine(opts => opts.BaseUrl = new("http://localhost:8181"))`. Requires `opa` process running on the host (README instructs `opa run -s policy.rego`). Issues one allowed + one denied `CreateAsync` + prints the audit-log rows (demonstrates the policy-denied → exit-3-equivalent path).
- Packages: `Vais.Agents.Control.Policy.Opa` + the usual control-plane set.
- ~250 LoC. Ships a local `policy.rego` (copy of `samples/opa-policies/model-provider-allowlist.rego`).
- README: *"Run OPA locally, gate agent creates on model-provider allowlist, watch denials hit the audit log."*
- Cross-link: `docs/guides/gate-agents-with-opa.md`.

**Doc-only sample: extend `samples/opa-policies/`** (already exists).
- Add `samples/opa-policies/time-window.rego` (example: only allow Invoke between 09:00–17:00 UTC).
- Add `samples/opa-policies/max-concurrent-runs.rego` (relies on `data.vais.agents.state.active_runs_by_tenant`).
- Update `samples/opa-policies/README.md` table.
- Stays doc-only; the existing 3 Rego samples from v0.14 are fine starters, these are enrichment.

### v0.15 — CLI

**Doc-only sample: `CliCookbook`** — directory with README + scripted recipes.
- No runnable console (CLI is the tool). Directory carries:
  - `recipes/apply-from-ci.sh` — non-interactive apply + error-handling.
  - `recipes/rollback-on-failed-apply.sh` — `vais apply; [[ $? != 0 ]] && vais delete <id> --force; vais apply -f prev.yaml`.
  - `recipes/stream-and-filter.sh` — `vais logs <id> --only turn.completed,tool.completed | tee run.log`.
  - `recipes/multi-context-deploy.sh` — `vais config use-context staging && vais apply -f … && vais config use-context prod && vais apply -f …`.
  - `sample-configs/` with 3 example `~/.vais/config.yaml` files (single-context, multi-context, tokenFile-rotation).
- README: *"Copy-paste recipes for common CLI workflows."*
- Cross-link: `docs/guides/apply-manifests-from-ci.md` + `docs/guides/tail-live-runs-with-vais-logs.md`.

---

## Samples index refresh (`samples/README.md`)

- [ ] Bump the header count from "21 runnable samples" to final count once new samples land. Target: **21 + 13 new runnable = 34 runnable**, plus 4 doc-only directories.
- [ ] Extend the index table with one row per new sample. Sort by pillar (v0.7 through v0.15 after the v0.1–v0.6 block).
- [ ] Extend the "Suggested learning path" with steps 11–15 covering the new surfaces:
  - 11. `McpServerStdio` → `McpServerHttp` — inbound MCP
  - 12. `A2AServerBasics` → `A2AInterruptResumeOrleans` — inbound A2A + durable tasks
  - 13. `AgentGraphInProcess` → `AgentGraphYamlLoader` → `AgentGraphMaf` → `AgentGraphResumeOnOrleans` — graph orchestration
  - 14. `StreamingFilterTypingIndicator` → `StreamingResiliencePolly` → `HttpStreamingInvoke` → `HttpStreamingCancellation` — streaming deepdive
  - 15. `HttpIdempotencyInMemory` → `OpenApiSpecExplorer` → `OpaPolicyGateLocal` → cookbook recipes — control-plane polish
- [ ] Update the `Build all` block with the new project paths.
- [ ] Add a "Tooling-only samples" section calling out `opa-policies/`, `opa-sidecar/`, `KubernetesOperatorQuickstart/`, `CliCookbook/`.

---

## Deliverable summary

| Pillar | New runnable | New doc-only | LoC (approx) |
|---|---|---|---|
| v0.7 MCP inbound | 2 | — | 330 |
| v0.8 A2A inbound | 2 | — | 390 |
| v0.9 Graph orchestration | 4 | — | 680 |
| v0.10 Streaming filter / resilience | 2 | — | 240 |
| v0.11 Idempotency + OpenAPI | 2 | — | 320 |
| v0.12 SSE streaming Invoke | 2 | — | 390 |
| v0.13 Kubernetes operator | — | 1 | — |
| v0.14 OPA policy engine | 1 | 1 (extend existing) | 250 |
| v0.15 CLI | — | 1 | — |
| **Total new** | **15 runnable** | **3 doc-only** | ~2600 LoC |

Plus version-bump sweep of **21 existing samples**.

Rough effort breakdown:
- **Runnable samples** — ~150 LoC average × 15 = ~2300 LoC. 3-4 hours per sample including README + expected-output recording + cross-link. **7-9 days**.
- **Doc-only samples** — ~1-2 hours each × 3 = ~0.5 day.
- **Version-bump sweep** — ~0.5 day (grep + sed + rebuild + fix-forward).
- **samples/README.md index refresh** — ~0.5 day.

**Total estimate: ~9-10 days focused work.** Natural split: 1 PR per pillar, ~9 PRs.

---

## Proposed PR shape

**Option A — feature-axis (recommended, mirrors docs plan):**
- **PR 1** — version-bump sweep (21 csprojs) + `samples/README.md` header/index refresh for existing samples.
- **PR 2** — v0.7 MCP inbound: 2 runnable samples.
- **PR 3** — v0.8 A2A inbound: 2 runnable samples (including Orleans interrupt-resume).
- **PR 4** — v0.9 graph orchestration: 4 runnable samples.
- **PR 5** — v0.10 + v0.12 streaming deep-dive: 4 runnable samples.
- **PR 6** — v0.11 HTTP polish: 2 runnable samples.
- **PR 7** — v0.14 OPA: 1 runnable sample + `opa-policies/` extension.
- **PR 8** — v0.13 + v0.15 doc-only samples + final `samples/README.md` learning-path refresh.

Each PR ends with a `build-all.sh` pass + manual run-through + expected-output captured in READMEs.

**Option B — bundle-by-layer:**
- PR 1: all version bumps + 21 existing samples verified.
- PR 2: all runnable samples for v0.7–v0.15 (15 new samples, big PR).
- PR 3: doc-only samples + index refresh.

Faster for one author; harder review.

Lean = **Option A**.

---

## Consistency rules to enforce across every new sample

- [ ] `Program.cs` starts with a top-comment block: one-line purpose, link to `docs/...`, `env` vars needed, `dotnet run --project ...` invocation.
- [ ] `README.md` structure: **Intro** (1 paragraph) → **Run** (shell block) → **Expected output** (code fence, verbatim) → **What it demonstrates** (bullet list of API surfaces exercised) → **Docs** (links). Mirrors existing sample READMEs.
- [ ] Use `Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance` when a sample needs a logger; avoid `AddLogging` if the sample doesn't emit logs.
- [ ] No DI container when the sample has fewer than 5 services; instantiate inline.
- [ ] Deterministic output — snapshot tests aren't needed, but the README's expected-output block stays accurate across re-runs.
- [ ] Exit code 0 on happy path; non-zero with a printed reason if a prerequisite is missing (e.g., `OPENAI_API_KEY` not set).
- [ ] All samples reference `0.15.0-preview` uniformly.

---

## Non-goals

- **End-to-end integration tests** gated on real K8s / real OPA / real LLM APIs. Integration tests live in `tests/*.IntegrationTests/`; samples are doc artifacts.
- **GUI demos** (web dashboard, TUI). Console only.
- **Performance benchmarks**. A `benchmarks/` tree would be separate.
- **Sample-test automation**. `build-all.sh` proves they compile; running each is a manual acceptance step.
- **Docker-compose bundles**. Each sample that needs infra documents the host-install path; orchestration via compose is a deploy-tree concern.
- **Cross-language samples** (Python / TS consumer of the HTTP API). Separate concern; may land alongside `samples/opa-sidecar/` or under a new `samples/clients/` tree later.

---

## Progress log

- 2026-04-20 — plan created. Inter-phase housekeeping companion to the docs plan. Covers the 9 post-v0.6 pillars shipped this and the prior session. Current `samples/` tree = 21 runnable (at `0.4.0-preview`, 11 releases stale) + 2 doc-only from v0.14. Target deliverable: 15 new runnable samples + 3 doc-only dirs + version-bump sweep of 21 existing, ~9-10 days focused work. Proposed PR shape = feature-axis, 8 PRs, aligned with the docs plan PR sequencing. **Pending**: start PR 1 (version-bump sweep + `samples/README.md` header refresh).
