# v0.19.0-preview — Graph as first-class deployable (Pillar D) — spike

Open-questions research doc for [Phase 3 Pillar D](./actor-agents-oss-phase-3-runtime-productisation.md#pillar-d--graph-as-a-first-class-deployable-us-5-us-6). Answers partner user-stories **US-5** (create a graph by code, deploy it like an agent) and **US-6** (create a graph in YAML, deploy it). Unblocks `POST /v1/graphs`, `vais apply -f graph.yaml`, and an `AgentGraph` CRD reconciled by the operator.

Written 2026-04-21. Precedes the findings + pillar plan of the same name.

---

## What v0.9 shipped, what's still in-process only

Pillar D inherits a deep graph runtime from v0.9. The gap is **deployability**, not correctness:

- ✅ `AgentGraphManifest` (Abstractions) — full wire format. `Id` / `Version` / `Entry` / `Nodes` / `Edges` / `StateSchema` / `MaxSteps` / `Labels` / `Annotations`. JSON-Schema-authored, YAML/JSON loaders shipped.
- ✅ `IAgentGraph<TState>` + `IAgentGraph` + `IResumableAgentGraph<TState>` (Abstractions) — invoke / stream / resume surface. Non-generic bag-state specialisation for YAML graphs.
- ✅ `InProcessGraphOrchestrator<TState>` (Core) — Pregel/BSP executor; super-step loop; predicate / effect / code-node resolvers; checkpoint-per-super-step; resume past interrupt nodes.
- ✅ `MafGraphOrchestrator` (Orchestration.Graph.MicrosoftAgentFramework) — MAF-backed executor parallel to in-process.
- ✅ `IGraphCheckpointer` + `GraphCheckpoint` + `InMemoryCheckpointer` (Abstractions / Core) — three-verb Save/Load/Delete surface.
- ✅ `GraphCheckpointGrain` + `OrleansCheckpointer` (Hosting.Orleans) — durable persistence; wired by default in `Runtime.Host` via `AddOrleansGraphCheckpointer()`.
- ✅ `YamlAgentGraphManifestLoader` + `JsonAgentGraphManifestLoader` + `AgentGraphManifestValidator` (Control.Manifests.Yaml / .Json) — authoring path for US-6.
- ✅ `ManifestResource.AgentGraphCase` (Control.Abstractions) — mixed-kind manifest batch support via discriminated union.

The gaps Pillar D closes:

- ❌ **No `IAgentGraphLifecycleManager`.** `IAgentLifecycleManager` is per-agent. Graph lifecycle (register manifest, instantiate on invoke, route resume-with-checkpoint, query run status) has no contract.
- ❌ **No `IAgentGraphRegistry`.** `IAgentRegistry` is `AgentManifest`-only. Nothing indexes `AgentGraphManifest` by id/version for `GET /v1/graphs/{id}` lookups.
- ❌ **No HTTP surface.** `Control.Http.Server` exposes `/v1/agents/*` only. No `/v1/graphs/*` group.
- ❌ **No control-plane client methods.** `IAgentControlPlaneClient` has no `CreateGraphAsync` / `InvokeGraphAsync` / `ResumeGraphAsync`.
- ❌ **No CRD.** `deploy/crds/vais.io_agents.yaml` exists; no `AgentGraph` CRD. Operator's `AgentEntityController` has no `AgentGraphEntity` sibling.
- ❌ **CLI kind-blind.** `ApplyCommand` assumes `kind: Agent` and uses `YamlAgentManifestLoader`. No `kind`-dispatching loader; no `invoke-graph` / `graph-logs` / `resume-graph` verbs.
- ❌ **No run correlation surface.** `InProcessGraphOrchestrator` internally mints `RunId` via `runIdFactory`; there is no "runs" store the HTTP layer can query ("show me runs for graph X", "what's the last checkpoint for run Y").

The three stacks the pillar ties together — **Lifecycle + HTTP + Operator** — each mirror a v0.13 precedent for Agents. The mechanical work is well-scoped; the design questions are about *shape*: what goes on the wire for invoke/resume, how runs are modelled, and whether graph CRs share or parallel the Agent CRD machinery.

---

## Scope fence — what ships in v0.19 vs. deferred

**v0.19 ships** the pure in-process deployability path — a YAML or code-authored graph lands on one runtime via `POST /v1/graphs`, gets invoked via `/v1/graphs/{id}/invoke`, streams events, checkpoints through interrupts, resumes via `/v1/graphs/{id}/resume`. Specifically:

- **`IAgentGraphLifecycleManager` contract** (new, in `Vais.Agents.Control.Abstractions`) — graph-shaped sibling of `IAgentLifecycleManager`. Verbs: Create / Update / Query / Invoke / InvokeStream / Resume / Cancel / Evict.
- **`IAgentGraphRegistry` contract** (new, in `Vais.Agents.Abstractions`) — graph-shaped sibling of `IAgentRegistry`. List / Get (by id + optional version).
- **`InMemoryAgentGraphRegistry` + `OrleansAgentGraphRegistry`** (Core + Hosting.Orleans) — storage backends parallel to Agent.
- **`AgentGraphLifecycleManager`** (Control.InProcess) — policy-gated, audited, routes verbs to `InProcessGraphOrchestrator<TState>` + `IGraphCheckpointer`.
- **HTTP surface** (Control.Http.Server) — `/v1/graphs/{id}` CRUD + `/v1/graphs/{id}/invoke` (sync) + `/v1/graphs/{id}/invoke/stream` (SSE) + `/v1/graphs/{id}/resume` (resume-from-checkpoint).
- **HTTP client** (Control.Http.Client) — `IAgentControlPlaneClient` grows graph verbs (additive; default interface methods so mocks don't break).
- **CRD** (deploy/crds) — `vais.io/v1alpha1/AgentGraph` parallel to `Agent`. Permissive v0.13-style schema with `x-kubernetes-preserve-unknown-fields: true`.
- **Operator** (Control.KubernetesOperator) — `AgentGraphEntity` + `AgentGraphEntityController` + `AgentGraphEntityFinalizer` parallel to Agent.
- **CLI** — `ApplyCommand` dispatches on `kind`; new `GetGraphsCommand`, `InvokeGraphCommand`, `ResumeGraphCommand`, `GraphLogsCommand`.
- **Docs** — `docs/concepts/graph-as-deployable.md` + `docs/guides/deploy-a-graph-to-the-runtime.md`. Sweeps: `architecture.md`, `packages.md`, `runtime-configuration.md`, `problem-details-urns.md`, `index.md`.
- **Sample** — `samples/GraphSupportTriage/` — one YAML graph with three agent nodes + a code node + an interrupt, deployable via `vais apply -f`.

**Explicitly deferred** to v0.20 (Pillar E) / v0.21 (Pillar F polish):

- **Cross-runtime graph refs** — `GraphNode.Ref: { id, runtime?, a2aUrl? }`. Pillar E.
- **Remote-registry adapters** — `HttpRemoteAgentRegistry`, A2A-backed registry. Pillar E.
- **MAF-backed graph orchestration over HTTP** — in-process orchestrator is the v0.19 default. `MafGraphOrchestrator` continues to work in-process; deciding orchestrator per-manifest via an opt-in field lands in a later version.
- **Graph-level policy / guardrails** — v0.17 runs them at the agent-node boundary. A first-class `GraphPolicySpec` (e.g., per-run budget ceiling) is not in scope.
- **Graph signals** — agents have `SignalAsync`; graphs may eventually need an analog for mid-run nudges. Out of scope until a partner asks.
- **Graph run history / auditing surface** — `GET /v1/graphs/{id}/runs` listing active + completed runs. Out of scope; caller correlates via `RunId` from invoke response + OTel.
- **Hot update mid-run** — updating a graph version while a run is in-flight. The in-flight run completes on the old version (mirrors Agent); no snapshot migration.
- **Checkpoint GC / retention policy** — terminal-checkpoint retention is explicit (v0.9) but no TTL or size cap. Pillar F polish.

---

## Blocking questions (10)

### Q1 — Where do the new contracts live?

**Context.** The pillar plan mentions `IAgentGraphLifecycleManager` in `Control.Abstractions` and `IAgentGraphRegistry` without specifying. Two candidate homes per contract:

- **Lifecycle manager** — `Vais.Agents.Control.Abstractions` (alongside `IAgentLifecycleManager`) **vs.** `Vais.Agents.Abstractions` (alongside `IAgentGraph`).
- **Registry** — `Vais.Agents.Abstractions` (alongside `IAgentRegistry`) **vs.** `Vais.Agents.Control.Abstractions`.

**Lean: Registry → `Abstractions`; Lifecycle manager → `Control.Abstractions`.** Matches the v0.6 precedent exactly. Registries are part of the core domain (tests consume them without pulling Control in); lifecycle managers are control-plane surface (policy + audit gating) and live with `IAgentPolicyEngine` / `IAuditLog` / `IAgentManifestLoader`.

No new package. `IGraphCheckpointer` already lives in `Abstractions`; `IAgentGraphRegistry` joins it.

### Q2 — Shape of `IAgentGraphLifecycleManager`

**Context.** `IAgentLifecycleManager` has 7 verbs (Create / Invoke / Signal / Query / Cancel / Update / Evict). A graph manager differs on three axes: invoke has typed vs. bag state; runs are correlated by `RunId` (not just agent-handle); resume has no agent analogue.

Option **A — mirror IAgentLifecycleManager 1-for-1 + add Resume**:

```csharp
public interface IAgentGraphLifecycleManager
{
    ValueTask<AgentGraphHandle> CreateAsync(AgentGraphManifest manifest, CancellationToken ct = default);
    ValueTask<AgentGraphHandle> UpdateAsync(AgentGraphHandle handle, AgentGraphManifest newManifest, CancellationToken ct = default);
    ValueTask<AgentGraphStatus> QueryAsync(AgentGraphHandle handle, CancellationToken ct = default);
    ValueTask<GraphInvocationResult> InvokeAsync(AgentGraphHandle handle, GraphInvocationRequest request, CancellationToken ct = default);
    IAsyncEnumerable<AgentGraphEvent> InvokeStreamAsync(AgentGraphHandle handle, GraphInvocationRequest request, CancellationToken ct = default);
    ValueTask<GraphInvocationResult> ResumeAsync(AgentGraphHandle handle, GraphResumeRequest request, CancellationToken ct = default);
    ValueTask CancelAsync(AgentGraphHandle handle, string runId, CancellationToken ct = default);
    ValueTask EvictAsync(AgentGraphHandle handle, CancellationToken ct = default);
}
```

Option **B — split into registry + orchestration interfaces**:
- `IAgentGraphRegistry` owns Create / Update / Query / Evict.
- `IAgentGraphOrchestrator` (new) owns Invoke / InvokeStream / Resume / Cancel.

Option **C — fold into IAgentLifecycleManager with `AgentOrGraphHandle` discriminator**. Rejected upfront — sibling-of-not-subtype decision already made in v0.9.

**Lean: A.** Symmetry with `IAgentLifecycleManager` is the operator/CLI's strongest signal. Splitting (B) forces consumers to wire two interfaces for every graph call; the in-process runtime has one engine anyway. Resume as a peer verb (not InvokeAsync overload) is the right model — it has a distinct request shape (`checkpointId` + `resumePayload`) and distinct audit record.

### Q3 — Typed state on the wire

**Context.** `IAgentGraph<TState>` is generic over an application POCO; `IAgentGraph` is the bag specialisation. HTTP has to carry state across a JSON boundary.

- **A. Bag-state only on the wire.** HTTP always sends/receives `Dictionary<string, JsonElement>`. Typed in-process consumers project at the seam. Matches the manifest's `StateSchema` story.
- **B. `JsonElement` body, typed POCO-via-reflection.** Server reflects the manifest's `StateSchema` (or a registered type binding) and round-trips the typed shape in/out. Complex.
- **C. Both, selected by content-type.** `application/json` → bag; `application/x-vais-typed+json` → typed POCO via declared handler. Over-designed for v0.19.

**Lean: A.** Wire is always bag. Code-first graphs (`IAgentGraph<TSupportState>`) still get to use typed state in-process; the HTTP invoker bridges the bag ↔ POCO at the edge via the manifest's `StateSchema` and the manifest's `handlerTypeName` (optional). Covers US-6 (YAML graphs are bag-native) without forcing US-5 graphs into a second wire shape.

### Q4 — Graph invocation / resume wire shapes

**Context.** The HTTP body needs to carry enough to kick a run (initial state, metadata) and, for resume, enough to continue from a checkpoint (checkpoint id, resume payload).

```csharp
public sealed record GraphInvocationRequest(
    IDictionary<string, JsonElement> InitialState,
    IReadOnlyDictionary<string, string>? Metadata = null,
    string? RunId = null,             // caller-supplied for correlation; runtime mints when null
    int? MaxSteps = null);            // override manifest ceiling, capped by policy

public sealed record GraphInvocationResult(
    string RunId,
    IDictionary<string, JsonElement> FinalState,
    bool IsComplete,                      // false = run paused on interrupt
    string? PendingInterruptId = null,    // matches GraphInterrupted.InterruptId; set when IsComplete == false
    string? PendingInterruptReason = null,
    string? PendingInterruptNodeId = null);

public sealed record GraphResumeRequest(
    string RunId,
    string InterruptId,                   // asserted against checkpoint.PendingInterruptId; mismatch → 409
    JsonElement? ResumePayload = null,
    IReadOnlyDictionary<string, string>? Metadata = null);
```

The resume key is `(RunId, InterruptId)` — see Q5. No `CheckpointId` on the wire; the checkpointer's existing `LoadAsync(runId)` is the single lookup path. `InterruptId` lets the server reject a resume that's addressing a stale interrupt (defensive today, load-bearing if parallel fan-out ever lands).

### Q5 — How the caller addresses a resume from SSE

**Context.** Phase-3 master plan open question: *How does the caller discover the checkpoint id after an interrupt on the streaming-invoke path?*

Reading `AgentGraphEvent.cs` settles part of this: `GraphInterrupted` already carries `(RunId, NodeId, InterruptId, Reason)` (v0.9 shape). `GraphCheckpoint.PendingInterruptId` on the persisted side matches `InterruptId`. The checkpointer is keyed on `RunId` alone — `LoadAsync(runId)` returns the latest checkpoint for that run, including `PendingInterruptId`.

So the caller already has what they need to resume; the question is **what we name the wire field**:

- **A. Resume by `RunId` alone.** `GraphResumeRequest(RunId, ResumePayload)`. Server loads latest checkpoint for run, asserts `PendingInterruptId is not null`, continues from `NextNodeId`. Minimum surface.
- **B. Resume by `(RunId, InterruptId)` pair.** Extra check that the caller is resuming the specific interrupt they saw, not a newer one. Defends against "two interrupts in a row, caller resumes the stale one" race (currently impossible with `InProcessGraphOrchestrator` — interrupts halt the run — but future parallel fan-out could).
- **C. Resume by opaque `CheckpointId` minted server-side.** Hides the shape. Caller gets `CheckpointId` in a new SSE `GraphPaused` event or in the `GraphInvocationResult`. Over-designed for v0.19's run-stops-at-interrupt model.

**Lean: B — resume by `(RunId, InterruptId)`.** Caller reads both from `GraphInterrupted` on the SSE stream (already carried) or from the sync `GraphInvocationResult` (add `PendingInterruptId` there). No new events, no new IDs. Server validates `checkpoint.PendingInterruptId == request.InterruptId` → rejects with `urn:vais-agents:graph-interrupt-mismatch` if not. Defensive and cheap.

This makes the previously-proposed `CheckpointId` wire field redundant — drop it from the wire shape; the `(RunId, InterruptId)` tuple *is* the resume key.

### Q6 — Registry key + version semantics

**Context.** `IAgentRegistry.GetAsync(id, version?)` — null version returns "latest lexicographical version". `AgentGraphManifest.Version` is declared but nothing indexes by it yet.

- **A. Mirror Agent exactly.** `IAgentGraphRegistry.GetAsync(id, version?)` with identical semantics. Updates produce new versions.
- **B. Single-version registry.** One graph id = one current version. Updates overwrite. Matches Kubernetes `replace` semantics.
- **C. Namespace-scoped.** `GetAsync(namespace, id, version?)` for eventual multi-tenancy. Premature.

**Lean: A.** Symmetry with Agent + free multi-version story for rolling graph rollouts. `POST /v1/graphs` creates version 1; `PATCH /v1/graphs/{id}` creates version N+1 with the new body.

### Q7 — Orleans grain shape for the graph registry

**Context.** `IAgentRegistry`'s Orleans backing is a `ManifestRegistryGrain`-style directory. Graph manifests are bigger (whole graph topology) but same shape.

- **A. Reuse `ManifestRegistryGrain`** with a new grain-key prefix (`"graph:"`). One grain type, one directory grain holds both kinds.
- **B. New `GraphManifestRegistryGrain`**. Parallel topology.
- **C. One grain per graph id** (a la `AiAgentGrain`). Overkill for a read-mostly registry.

**Lean: B.** `IAgentRegistry` and `IAgentGraphRegistry` are distinct interfaces; backing grains should be too. Reuse of the directory-grain machinery is fine (copy-paste small, test coverage parallel). A avoids grain code duplication but adds a discriminator inside the grain that's more annoying than the parallel path.

### Q8 — CRD shape for AgentGraph

**Context.** `deploy/crds/vais.io_agents.yaml` uses hand-rolled v0.13-style schema with `x-kubernetes-preserve-unknown-fields: true` because KubeOps 10.3.4's transpiler can't handle `TimeSpan`. `AgentGraphManifest` is larger but has no TimeSpan (none of `GraphNode` / `GraphEdge` / `GraphEdgePredicate` / `GraphEdgeEffect` touch time).

- **A. Hand-rolled permissive schema** — same style as `vais.io_agents.yaml`. `x-kubernetes-preserve-unknown-fields: true` on `spec`. Ships same day.
- **B. Auto-transpiled tight schema** — KubeOps emits full validation. Requires proving the transpiler handles `AgentGraphManifest`'s record types + nested `GraphEdgePredicate` union. Medium risk; slows the pillar.
- **C. Hybrid** — hand-rolled today, open a follow-up ticket for tight schema after KubeOps upstream fixes. Pragmatic.

**Lean: C (which degenerates to A for v0.19).** Partner value is "apply a graph CR and it routes to the control plane" — schema tightness is quality, not feature. Ship with A's shape; document the gap. Matches Agent's v0.13 trajectory.

### Q9 — Operator reconcile loop for AgentGraph

**Context.** `AgentEntityController` implements a 6-row decision table against `IAgentControlPlaneClient` for Agents. Graph version is mechanically parallel.

- **A. Copy-paste + rename.** `AgentGraphEntityController` with `IAgentControlPlaneClient.CreateGraphAsync` / `UpdateGraphAsync` etc. Duplicates the decision table.
- **B. Extract `ControlPlaneEntityController<TEntity, THandle>` generic base.** Both controllers inherit. Tighter, risk of premature abstraction since no third resource kind exists.
- **C. Shared static decision-table helper.** Both controllers delegate to `ReconcileDecisionTable.Route(entity, spec, handle)`. Middle ground.

**Lean: A for v0.19.** Two controllers is still "two"; the abstraction cost lands when we either have a third (unlikely) or when a bug forces us to change both. The copy is ~150 lines; duplication risk is low because the shape is stable.

Revisit in Pillar F if Pillar E adds a third reconcilable kind.

### Q10 — CLI dispatch + new verbs

**Context.** `ApplyCommand` today assumes `kind: Agent`. `vais invoke` is agent-only. `vais get agents` is agent-only. We want `vais apply -f graph.yaml` to work symmetrically + new graph-shaped verbs.

- **Apply dispatch:**
  - **A.** Sniff the manifest's `kind` field; load via `YamlAgentManifestLoader` or `YamlAgentGraphManifestLoader` accordingly. Already supported by `LoadAllResourcesFromStringAsync` — it emits `ManifestResource.AgentCase` / `AgentGraphCase`.
  - **B.** Separate `vais apply-graph` verb. Rejected — breaks the kubectl-style ergonomic.

- **New verbs (parallel to existing):**
  - `vais get graphs` (parallel to `vais get agents`).
  - `vais invoke-graph <id> --initial-state @state.json [--stream] [--resume-from <checkpointId> --resume-payload @payload.json]`.
  - `vais graph-logs <id> --run-id <runId>`.
  - `vais delete graph <id>` (or `--kind graph` on existing delete).

- **CLI resume shape:**
  - **A.** Separate `resume-graph` verb. Explicit.
  - **B.** `invoke-graph --resume-from <checkpointId>` flag. Shares one verb. Partner-ergonomic.

**Lean: Apply = A (sniff kind); verbs parallel Agent shape; resume = B flag on `invoke-graph`.** Mirrors kubectl where `apply` is kind-agnostic + `kubectl get <kind>` is the norm. Resume-as-flag avoids a new top-level verb for a function that's "keep invoking, but from here".

---

## Design decisions — proposed summary table

| # | Decision | Choice |
|---|---|---|
| Q1 | Contract homes | Registry → `Abstractions`; Lifecycle manager → `Control.Abstractions` |
| Q2 | Lifecycle-manager shape | Mirror `IAgentLifecycleManager` 1-for-1 + peer `ResumeAsync` verb |
| Q3 | State on the wire | Bag-state only; typed POCOs stay in-process |
| Q4 | Invoke/resume wire shapes | `GraphInvocationRequest` / `GraphInvocationResult` / `GraphResumeRequest`; resume keyed on `(RunId, InterruptId)` |
| Q5 | Resume addressing from SSE | Reuse existing `GraphInterrupted(RunId, InterruptId)`; server asserts `checkpoint.PendingInterruptId == request.InterruptId` |
| Q6 | Registry version semantics | Mirror Agent exactly — latest-lexicographical, new-version-on-update |
| Q7 | Orleans registry grain | New `GraphManifestRegistryGrain` parallel to Agent |
| Q8 | CRD shape | Hand-rolled permissive schema, parallel to Agent v0.13 |
| Q9 | Operator reconciler | Copy-paste-rename `AgentGraphEntityController`; revisit abstraction in Pillar F |
| Q10 | CLI dispatch + verbs | Apply sniffs kind; parallel `get graphs` / `invoke-graph` / `graph-logs`; resume = flag on `invoke-graph` |

---

## Open risks + mitigations

- **Risk: none on `GraphInterrupted` event shape.** Verified on read: `GraphInterrupted(RunId, NodeId, InterruptId, Reason)` already carries the resume key. Q5 decision (resume by `(RunId, InterruptId)`) reuses existing fields — zero Abstractions surface change for events.
- **Risk: `IAgentControlPlaneClient` grows 6-8 new methods.** Mirror shape is self-consistent; default-interface-methods keep all existing mocks compiling. **Mitigation:** every new method ships a `NotSupportedException`-throwing default (matches v0.11's `InvokeStreamAsync` precedent).
- **Risk: CRD schema drift between Agent and AgentGraph.** Copy-paste style means a schema fix in one doesn't propagate. **Mitigation:** document the parallel in a `deploy/crds/README.md` (new); Pillar F revisit.
- **Risk: `InProcessGraphOrchestrator` caches per-manifest in the host.** Every `POST /v1/graphs/{id}/invoke` instantiates a new orchestrator — cheap but allocates. **Mitigation:** measure; if hot, cache by `(graphId, graphVersion)` like v0.17 caches `StatefulAgentOptions`.
- **Risk: Two Orleans-backed registries double the startup cost of `AddOrleansAgentRegistry()`.** Both are lazy directory grains; startup cost is negligible. **Mitigation:** verify in composition-root test.
- **Risk: `GraphInvocationRequest.InitialState` as `IDictionary<string, JsonElement>`** may not round-trip cleanly through System.Text.Json default converters (dictionary values). **Mitigation:** v0.9 checkpointer proves this works; use the same `JsonSerializerOptions.Default` path.

---

## Integration-test strategy preview

Parallel to v0.17's `ManifestInstantiationIntegrationTests` + v0.18's `PluginLoadingIntegrationTests`:

- **Unit:** 20+ `AgentGraphLifecycleManagerTests` — policy gating / audit emission / verb routing / cache invalidation on update / resume past interrupt / cancel mid-run.
- **Registry:** `InMemoryAgentGraphRegistryTests` + `OrleansAgentGraphRegistryTests` — list / get / versioning / concurrent updates.
- **HTTP:** `GraphControlPlaneEndpointTests` — one test per endpoint; WebApplicationFactory + fake lifecycle manager; Problem-Details mapping for 400/404/409/422; SSE contract test for invoke/stream.
- **CLI:** `ApplyCommandTests` — mixed-kind YAML dispatches both; `InvokeGraphCommandTests` — JSON body + SSE streaming + resume flag.
- **End-to-end:** `GraphLifecycleIntegrationTests` — apply-invoke-interrupt-resume-complete against a fake `IAiAgent` registered as a graph node's target, using real `InMemoryAgentRegistry` + `InMemoryCheckpointer` + real HTTP routing. Mirrors v0.17's integration test in spirit.
- **Operator:** one happy-path `AgentGraphEntityControllerTests` — CR-apply-to-client-call round-trip with a mocked `IAgentControlPlaneClient`. Parity with `AgentEntityControllerTests`.

Target: 22 → 24 test projects (no new projects; tests fold into existing Control.Http.Tests / CrossHostTests / etc.).

---

## PR breakdown preview

Matches v0.17 / v0.18 four-PR cadence:

- **PR 1** — Library layer: contracts (`IAgentGraphLifecycleManager`, `IAgentGraphRegistry`, `GraphInvocationRequest/Result/Resume`, optional `CheckpointId` on `GraphInterrupted`), InMemory + Orleans registries, `AgentGraphLifecycleManager`, HTTP surface + routes + Problem-Details mapping, `IAgentControlPlaneClient` graph verbs. PublicAPI.Unshipped. ~60 tests.
- **PR 2** — Runtime.Host wiring: `CompositionRoot.AddAgentGraphControlPlane()` calls; `appsettings.json` additions if any; composition-root ordering test for graph registries. Sample `samples/GraphSupportTriage/` with YAML + README. ~8 tests.
- **PR 3** — Operator + CRD: `deploy/crds/vais.io_agentgraphs.yaml`; `AgentGraphEntity` + `AgentGraphEntityController` + `AgentGraphEntityFinalizer` + `AgentGraphSpec` + `AgentGraphStatus`; Helm chart `--set crds.graphs=true` toggle. ~12 tests.
- **PR 4** — Docs + CLI: `docs/concepts/graph-as-deployable.md` + `docs/guides/deploy-a-graph-to-the-runtime.md`; sweeps across architecture / packages / runtime-configuration / problem-details-urns / index / installation; `vais apply` kind-dispatch; new `GetGraphsCommand` / `InvokeGraphCommand` / `GraphLogsCommand`; PublicAPI.Shipped promotion; milestone log; phase-3 plan tick.

Tag `v0.19.0-preview` on the PR 4 commit (or on the runtime-host+docs commit if we split host wiring into a separate stage, matching v0.18's two-commit bundle).

---

## Next steps

1. **User confirms direction** on any of the 10 questions where the lean is non-obvious (Q3, Q5, Q7, Q9 are the most opinion-loaded).
2. Draft **findings doc** (`plans/actor-agents-oss-v0.19-graph-as-deployable-findings.md`) locking the decisions + capturing the API surface diff at statement granularity.
3. Draft **pillar plan** (`plans/actor-agents-oss-v0.19-graph-as-deployable-pillar.md`) with PR-by-PR task lists.
4. PR 1 starts.

No implementation work until the spike → findings → pillar sequence clears. Matches v0.16 / v0.17 / v0.18 cadence.
