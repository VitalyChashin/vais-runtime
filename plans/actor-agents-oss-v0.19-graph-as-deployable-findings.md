# v0.19.0-preview — Graph as first-class deployable (Pillar D) — findings

Decisions-locked follow-up to [the spike](./actor-agents-oss-v0.19-graph-as-deployable-spike.md). Captures the 10 blocking-question answers plus statement-level API surface diff the [pillar plan](./actor-agents-oss-v0.19-graph-as-deployable-pillar.md) consumes.

Written 2026-04-21. User confirmed spike direction; answers below are the agreed shape.

---

## Decision log (Q1–Q10)

| # | Question | Decision | Rationale |
|---|---|---|---|
| Q1 | Contract homes | `IAgentGraphRegistry` → `Vais.Agents.Abstractions`; `IAgentGraphLifecycleManager` → `Vais.Agents.Control.Abstractions`. | Mirrors the v0.6 split exactly. Registry is core-domain (tests consume without Control); lifecycle manager is control-plane surface. |
| Q2 | Lifecycle-manager shape | Mirror `IAgentLifecycleManager` 1-for-1 + peer `ResumeAsync` verb. | Operator + CLI + docs share the agent shape. Resume is a distinct request (not an overload) because the audit record differs. |
| Q3 | Wire state shape | Bag (`IDictionary<string, JsonElement>`) on every HTTP surface. Typed POCOs stay in-process. | Manifest's `StateSchema` is the source of truth. US-5 typed-state code projects at the seam; US-6 YAML is bag-native. |
| Q4 | Invoke / resume wire records | `GraphInvocationRequest` + `GraphInvocationResult` + `GraphResumeRequest`; resume key = `(RunId, InterruptId)`. | Reuses `GraphInterrupted.InterruptId` which v0.9 already carries. No new `CheckpointId` concept. |
| Q5 | Resume addressing from SSE | Caller reads `RunId` + `InterruptId` directly off the emitted `GraphInterrupted` event. Server asserts `checkpoint.PendingInterruptId == request.InterruptId`; mismatch → HTTP 409 `urn:vais-agents:graph-interrupt-mismatch`. | Zero event-shape change. Defensive against future parallel fan-out; free today. |
| Q6 | Registry version semantics | Mirror Agent: `GetAsync(id, version?)`; null version returns latest-lexicographical. Updates publish a new version. | Free rolling-rollout story. Consistency is more valuable than novelty. |
| Q7 | Orleans registry grain topology | New `GraphManifestRegistryGrain` + `GraphManifestRegistryDirectoryGrain`. Parallel to Agent, not shared. | Distinct interfaces → distinct grains. Copy-paste small; discriminator-in-grain is more annoying than parallel code. |
| Q8 | CRD schema | Hand-rolled permissive schema (`x-kubernetes-preserve-unknown-fields: true` on `spec`). Parallel to `vais.io_agents.yaml` v0.13 style. | KubeOps transpiler risk on graph records is non-zero; tight schema is quality, not feature. Revisit in Pillar F. |
| Q9 | Operator reconciler | Copy-paste-rename `AgentGraphEntityController` / `AgentGraphEntityFinalizer`. Revisit `ControlPlaneEntityController<T,H>` abstraction in Pillar F if Pillar E adds a third kind. | Two controllers is not worth a base class. ~150 lines of parallel code. |
| Q10 | CLI dispatch + verbs | `vais apply` sniffs `kind` via existing `LoadAllResourcesFromStringAsync` → `ManifestResource.AgentCase` / `AgentGraphCase`. Verbs parallel Agent: `get graphs`, `invoke-graph [--resume-from <interruptId>]`, `graph-logs`. Resume is a flag on `invoke-graph`, not a new verb. | kubectl-style ergonomics. Resume-as-flag avoids a new top-level command for what is mechanically "keep invoking from here". |

---

## Wire contract — locked shapes

```csharp
// Vais.Agents.Abstractions

public interface IAgentGraphRegistry
{
    IAsyncEnumerable<AgentGraphManifest> ListAsync(string? labelPrefix = null, CancellationToken cancellationToken = default);
    ValueTask<AgentGraphManifest?> GetAsync(string id, string? version = null, CancellationToken cancellationToken = default);
}

public sealed record AgentGraphHandle(string GraphId, string Version);

public sealed record AgentGraphStatus(
    string GraphId,
    string Version,
    int ActiveRunCount,
    int CompletedRunCount,
    int PendingInterruptCount,
    DateTimeOffset? LastInvokedAt);

public sealed record GraphInvocationRequest(
    IDictionary<string, JsonElement> InitialState,
    IReadOnlyDictionary<string, string>? Metadata = null,
    string? RunId = null,
    int? MaxSteps = null);

public sealed record GraphInvocationResult(
    string RunId,
    IDictionary<string, JsonElement> FinalState,
    bool IsComplete,
    string? PendingInterruptId = null,
    string? PendingInterruptNodeId = null,
    string? PendingInterruptReason = null);

public sealed record GraphResumeRequest(
    string RunId,
    string InterruptId,
    JsonElement? ResumePayload = null,
    IReadOnlyDictionary<string, string>? Metadata = null);
```

```csharp
// Vais.Agents.Control.Abstractions

public interface IAgentGraphLifecycleManager
{
    ValueTask<AgentGraphHandle> CreateAsync(AgentGraphManifest manifest, CancellationToken ct = default);
    ValueTask<AgentGraphHandle> UpdateAsync(AgentGraphHandle handle, AgentGraphManifest newManifest, CancellationToken ct = default);
    ValueTask<AgentGraphStatus> QueryAsync(AgentGraphHandle handle, CancellationToken ct = default);
    ValueTask<GraphInvocationResult> InvokeAsync(AgentGraphHandle handle, GraphInvocationRequest request, CancellationToken ct = default);
    IAsyncEnumerable<AgentGraphEvent> InvokeStreamAsync(AgentGraphHandle handle, GraphInvocationRequest request, CancellationToken ct = default);
    ValueTask<GraphInvocationResult> ResumeAsync(AgentGraphHandle handle, GraphResumeRequest request, CancellationToken ct = default);
    IAsyncEnumerable<AgentGraphEvent> ResumeStreamAsync(AgentGraphHandle handle, GraphResumeRequest request, CancellationToken ct = default);
    ValueTask CancelAsync(AgentGraphHandle handle, string runId, CancellationToken ct = default);
    ValueTask EvictAsync(AgentGraphHandle handle, CancellationToken ct = default);
}
```

**Invariants** (enforced by the in-process impl + asserted in tests):

- `CreateAsync` on a `(graphId, version)` that already exists → HTTP 409 `urn:vais-agents:graph-conflict`.
- `UpdateAsync` always publishes a new version; the supplied `handle.Version` is the *prior* version (same precedent as Agent `UpdateAsync`).
- `InvokeAsync` with a caller-supplied `RunId` that collides with an active run → HTTP 409 `urn:vais-agents:graph-run-conflict`.
- `ResumeAsync` with `InterruptId` that does not match `checkpoint.PendingInterruptId` → HTTP 409 `urn:vais-agents:graph-interrupt-mismatch`.
- `ResumeAsync` against a completed checkpoint (`IsComplete == true`) → HTTP 409 `urn:vais-agents:graph-already-complete`.
- `ResumeAsync` against a missing run → HTTP 404 `urn:vais-agents:graph-run-not-found`.
- `EvictAsync` deletes the manifest *and* all checkpoints for its runs (via `IGraphCheckpointer.DeleteAsync`).
- `CancelAsync` marks the run cancelled; a concurrently-awaiting streaming invoke receives a final `GraphFailed` event with `ErrorType = "Cancelled"`.

---

## Statement-level API surface diff

Targets all 27 existing packages. Net ship: **no new packages, no new projects**. Everything lands on existing packages. PublicAPI analyzer discipline: additions go into `PublicAPI.Unshipped.txt` in PR 1–3; PR 4 promotes to `PublicAPI.Shipped.txt`.

### Vais.Agents.Abstractions (+30 surface items)

New files:
- `IAgentGraphRegistry.cs` — 1 interface, 2 members.
- `AgentGraphHandle.cs` — 1 record, 2 properties.
- `AgentGraphStatus.cs` — 1 record, 6 properties.
- `GraphInvocationRequest.cs` — 1 record, 4 properties.
- `GraphInvocationResult.cs` — 1 record, 6 properties.
- `GraphResumeRequest.cs` — 1 record, 4 properties.

**No modifications** to `AgentGraphEvent.cs`, `AgentGraphManifest.cs`, `IAgentGraph.cs`, `IResumableAgentGraph.cs`, `IGraphCheckpointer.cs`. All additive.

### Vais.Agents.Control.Abstractions (+10 surface items)

New files:
- `IAgentGraphLifecycleManager.cs` — 1 interface, 9 members.

No changes to `IManifestApplyDiagnosticsSink` or other v0.18 contracts.

### Vais.Agents.Core (+4 surface items)

New files:
- `InMemoryAgentGraphRegistry.cs` — 1 class, 3 public members (`Register`, `Remove`, plus the two interface methods). Thread-safe via `ConcurrentDictionary<(string, string), AgentGraphManifest>`. Parallels `InMemoryAgentRegistry` line-for-line.

### Vais.Agents.Control.InProcess (+6 surface items)

New files:
- `AgentGraphLifecycleManager.cs` — 1 class. Policy-gated, audited. Routes each verb via `IAgentPolicyEngine` + `IAuditLog`; delegates Invoke/Resume to `InProcessGraphOrchestrator<IDictionary<string, JsonElement>>` constructed per-invoke. Run correlation state held in `ConcurrentDictionary<(string, string, string), RunState>` keyed by `(graphId, version, runId)`.

Existing `PolicyOperation` enum in Control.Abstractions: add `GraphCreate`, `GraphUpdate`, `GraphQuery`, `GraphInvoke`, `GraphResume`, `GraphCancel`, `GraphEvict` entries (additive; no renumbering of existing values). **Correction**: `PolicyOperation` lives in `Control.Abstractions` per v0.6 — additions bump that package's Unshipped.txt.

### Vais.Agents.Hosting.Orleans (+8 surface items)

New files:
- `IGraphManifestRegistryGrain.cs` — 1 interface, 4 methods.
- `GraphManifestRegistryGrain.cs` — 1 class.
- `IGraphManifestRegistryDirectoryGrain.cs` — 1 interface, 3 methods.
- `GraphManifestRegistryDirectoryGrain.cs` — 1 class.
- `OrleansAgentGraphRegistry.cs` — 1 class; mirrors `OrleansAgentRegistry`'s JSON-string-at-the-grain-boundary shape.
- `AgenticHostingOrleansServiceCollectionExtensions.cs`: add `AddOrleansAgentGraphRegistry(this IServiceCollection services)` extension method.

Serialisation: `AgentGraphManifest` round-trips through `JsonSerializerOptions.Default`; includes `JsonElement`-valued `StateSchema` field. Verified to serialise cleanly via the v0.9 checkpointer.

### Vais.Agents.Control.Http.Server (+12 surface items + 8 endpoint rows)

Modifications to `AgentControlPlaneEndpointRouteBuilderExtensions.cs`:
- New endpoint group `/v1/graphs`:
  - `POST /graphs` — Create, accepts YAML/JSON bodies.
  - `GET /graphs` — List with label prefix + limit query.
  - `GET /graphs/{id}` — Query (manifest + status + handle bundle).
  - `PATCH /graphs/{id}` — Update.
  - `DELETE /graphs/{id}?mode=cancel&runId=...` or `?mode=evict` — Cancel / Evict.
  - `POST /graphs/{id}/invoke` — Sync invoke; body = `GraphInvocationRequest`; response = `GraphInvocationResult`.
  - `POST /graphs/{id}/invoke/stream` — SSE stream; body = `GraphInvocationRequest`; SSE events = `AgentGraphEvent` taxonomy.
  - `POST /graphs/{id}/resume` — Sync resume; body = `GraphResumeRequest`; response = `GraphInvocationResult`.
  - `POST /graphs/{id}/resume/stream` — SSE stream variant of resume.

New files:
- `GraphContracts.cs` — `AgentGraphQueryResponse`, `AgentGraphListResponse` (mirrors `Contracts.cs`).
- `AgentGraphEventSerializer.cs` — SSE event-name map for the `AgentGraphEvent` taxonomy.

Modifications:
- `AgentControlPlaneServiceCollectionExtensions.cs` — `AddAgentControlPlane(...)` grows a `IAgentGraphLifecycleManager` registration path; default wires to `AgentGraphLifecycleManager` when `IAgentGraphRegistry` + `IGraphCheckpointer` are in DI.
- `ProblemDetailsMapping.cs` — map four new exception types: `GraphAlreadyCompleteException`, `GraphInterruptMismatchException`, `GraphRunNotFoundException`, `GraphRunConflictException` (new exception types shipped from Control.Abstractions alongside the manager interface).

### Vais.Agents.Control.Http.Client (+16 surface items)

Modifications to `IAgentControlPlaneClient.cs` (additive; default interface methods so existing mocks don't break):
- `CreateGraphAsync(AgentGraphManifest, string? idempotencyKey, CancellationToken)` + no-key overload.
- `ListGraphsAsync(string? labelPrefix, int? limit, CancellationToken)`.
- `QueryGraphAsync(string graphId, string? version, CancellationToken)`.
- `UpdateGraphAsync(string graphId, AgentGraphManifest, string? version, string? idempotencyKey, CancellationToken)`.
- `CancelGraphAsync(string graphId, string runId, string? idempotencyKey, CancellationToken)`.
- `EvictGraphAsync(string graphId, string? version, string? idempotencyKey, CancellationToken)`.
- `InvokeGraphAsync(string graphId, GraphInvocationRequest, string? version, string? idempotencyKey, CancellationToken)`.
- `InvokeGraphStreamAsync(...)` — yields `AgentGraphEvent`.
- `ResumeGraphAsync(string graphId, GraphResumeRequest, string? version, string? idempotencyKey, CancellationToken)`.
- `ResumeGraphStreamAsync(...)` — yields `AgentGraphEvent`.

Concrete `AgentControlPlaneClient` implements all non-default methods; default methods on the interface throw `NotSupportedException` (matches v0.11 `InvokeStreamAsync` precedent).

`WireTypes.cs` — internal DTOs if any serialisation shape drifts; expected zero change — the wire records in Abstractions are the DTOs.

### Vais.Agents.Runtime.Instantiation — no changes

v0.17's `AgentManifestTranslator` is agent-only; graphs are *consumers* of agent manifests, not translated themselves. A graph's `Agent`-kind node resolves its target agent via `IAgentRegistry` at run time; the per-node agent is already instantiated by v0.17's translator when the node fires.

### Vais.Agents.Runtime.Plugins — no changes

v0.18's plugin loader is agent-only. Graph "Code"-kind nodes use a `IGraphCodeNode` resolver that is separate from the plugin registry (v0.9 shipped `Func<GraphHandlerRef, IGraphCodeNode>?` on the orchestrator ctor). Exposing graph code nodes through the plugin loader is a v0.19.x follow-up.

### Vais.Agents.Runtime.Host (+~12 lines in CompositionRoot)

Modifications to `CompositionRoot.cs`:
- After `AddOrleansAgentRegistry()`: `services.AddOrleansAgentGraphRegistry();`.
- New block registering `IAgentGraphLifecycleManager`:
  ```csharp
  services.AddSingleton<IAgentGraphLifecycleManager>(sp => new AgentGraphLifecycleManager(
      sp.GetRequiredService<IAgentGraphRegistry>(),
      sp.GetRequiredService<IAgentRegistry>(),
      sp.GetRequiredService<IAgentLifecycleManager>(),
      sp.GetRequiredService<IGraphCheckpointer>(),
      policy: sp.GetService<IAgentPolicyEngine>(),
      audit: sp.GetService<IAuditLog>(),
      contextAccessor: sp.GetService<IAgentContextAccessor>(),
      logger: sp.GetService<ILogger<AgentGraphLifecycleManager>>() ?? NullLogger<AgentGraphLifecycleManager>.Instance));
  ```

No `RuntimeOptions` changes — graphs inherit the existing clustering / observability / OPA wiring.

`appsettings.json` — no new keys; graph endpoints mount under the same `/v1` prefix as agents.

### Vais.Agents.Control.KubernetesOperator (+~12 surface items)

New files:
- `AgentGraphEntity.cs` — `k8s.Models.CustomResource<AgentGraphSpec, AgentGraphStatusSnapshot>`. `[KubernetesEntity(Group = "vais.io", Kind = "AgentGraph", ApiVersion = "v1alpha1", PluralName = "agentgraphs")]`.
- `AgentGraphSpec.cs` — mirrors `AgentGraphManifest` projection + optional `secretRefs` (v0.14 parity; empty for v0.19 since graphs don't own secrets).
- `AgentGraphStatusSnapshot.cs` — phase + conditions + `ActiveRunCount` + `PendingInterruptCount` + `LastReconciledAt`.
- `AgentGraphEntityController.cs` — copy-paste rename of `AgentEntityController` with `CreateGraphAsync` / `UpdateGraphAsync` dispatch; 6-row decision table unchanged in structure.
- `AgentGraphEntityFinalizer.cs` — parallel to `AgentEntityFinalizer`; calls `EvictGraphAsync` on delete.
- `AgentGraphSpecProjector.cs` — CR-spec → `AgentGraphManifest` projection.
- `AgentGraphSpecHasher.cs` — mirror of `SpecHasher` for graph-spec hash-based diff detection.

Modifications:
- `AgentKubernetesOperatorServiceCollectionExtensions.cs` — `AddAgentKubernetesOperator(...)` also registers graph controllers + finalizer when `options.ReconcileGraphs ?? true` is set. Default-on because graphs are the headline v0.19 feature.

### Vais.Agents.Control.KubernetesOperator.Host

`Program.cs` — if the existing entity-controller registration pattern auto-discovers, zero changes. Otherwise one line adding `AgentGraphEntity` to the KubeOps builder. **Verify in PR 3.**

### Vais.Agents.Cli (+4 commands, ~150 LOC apply-dispatch rewrite)

Modifications:
- `Commands/ApplyCommand.cs` — replace single-loader path with `LoadAllResourcesFromStringAsync` → foreach `ManifestResource` → dispatch on `AgentCase` vs `AgentGraphCase`. Existing idempotency-key + token-resolution unchanged.
- `Commands/GetAgentsCommand.cs` — refactor to a `GetCommand` that dispatches on `--kind agents|graphs` (retain `vais get agents` alias for source compat; add `vais get graphs`).

New files:
- `Commands/InvokeGraphCommand.cs` — `vais invoke-graph <id> [--initial-state @state.json] [--stream] [--resume-from <interruptId>] [--resume-payload @payload.json]`.
- `Commands/GraphLogsCommand.cs` — `vais graph-logs <id> --run-id <runId>`. Defers to the existing event-bus SSE with graph scope.
- `Commands/DeleteCommand.cs` — grow `--kind graph|agent` dispatch; default `agent` for source compat.

`Program.cs` — register the new commands in the Spectre.Console command table.

### Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework — no changes

In-process orchestrator (v0.9 Core) is the v0.19 deployment path; MAF-backed is an in-process-only alternative. Exposing MAF via the HTTP surface is a later pillar — requires bridging MAF's `CheckpointManager` to `IGraphCheckpointer`, which v0.9 findings already flag as non-trivial.

### deploy/ (+ 2 files)

- `deploy/crds/vais.io_agentgraphs.yaml` — new, hand-rolled schema parallel to `vais.io_agents.yaml`.
- `deploy/helm/vais-agents-runtime/templates/` — no net-new templates; graph endpoints live on the same deployment.
- `deploy/helm/vais-agents-runtime/values.yaml` — add `crds.agentGraphs.install: true` toggle parallel to the existing agents toggle.

### docs/ (+ 2 new, ~8 sweeps)

New:
- `docs/concepts/graph-as-deployable.md` — concept page: what's a deployable graph, wire shape, interrupt-resume flow, how CRD reconciles.
- `docs/guides/deploy-a-graph-to-the-runtime.md` — step-by-step parallel to `author-an-agent-in-yaml.md`.

Sweeps:
- `docs/concepts/architecture.md` — expand to 29-package view (27 stays; two new per-controller runtime rows + graph-control-plane HTTP arm); add graph lifecycle box to mermaid.
- `docs/concepts/declarative-agents.md` — note how agent nodes inside graphs reuse the declarative path; one-paragraph cross-reference.
- `docs/concepts/runtime-plugins.md` — note `IGraphCodeNode` + `Func<GraphHandlerRef, IGraphCodeNode>` resolver stays separate from plugin loader (distinct v0.19.x follow-up).
- `docs/reference/packages.md` — no new packages; update per-package rows with graph-related surface.
- `docs/reference/runtime-configuration.md` — add "no new env vars for graphs" callout + link to the CRD-toggle in values.yaml.
- `docs/reference/problem-details-urns.md` — add v0.19 group: `graph-conflict`, `graph-run-conflict`, `graph-interrupt-mismatch`, `graph-already-complete`, `graph-run-not-found`, `graph-validation-failed`.
- `docs/index.md` — graph-as-deployable concept row + deploy-a-graph guide row + quick-map entry.
- `docs/guides/install-the-runtime-locally.md` — one-line mention: `/v1/graphs/*` available alongside `/v1/agents/*`.
- `docs/guides/deploy-the-runtime-to-kubernetes.md` — add graph CRD install step.

### tests/ — 5 test projects touched, 0 new projects

- **Vais.Agents.Core.Tests** — `InMemoryAgentGraphRegistryTests` (~10).
- **Vais.Agents.Hosting.Orleans.Tests** — `OrleansAgentGraphRegistryTests` (~10).
- **Vais.Agents.Control.Http.Tests** — `AgentGraphLifecycleManagerTests` (~20), `GraphControlPlaneEndpointTests` (~18 — one per endpoint + Problem-Details mapping), `AgentGraphEventSerializerTests` (~5).
- **Vais.Agents.Control.KubernetesOperator.Tests** — `AgentGraphEntityControllerTests` (~12 happy + failure paths mirroring the Agent suite).
- **Vais.Agents.Cli.Tests** — `ApplyCommandGraphDispatchTests` (~6), `InvokeGraphCommandTests` (~8), `GraphLogsCommandTests` (~4).
- **Vais.Agents.CrossHostTests** — `GraphLifecycleIntegrationTests` (~6 happy-path: apply → invoke → interrupt → resume → complete against real Orleans TestCluster + real YAML loader + fake agents).
- **Vais.Agents.Runtime.Host.Tests** — 1 composition-root ordering test asserting graph-registry + graph-lifecycle-manager bind in the right order.

Total new tests: ~99 unit + ~6 integration. 22 → 22 test projects (folds into existing projects).

---

## New exception types (shipped from Control.Abstractions)

Match the existing `AgentManifestValidationException` + `AgentPolicyDeniedException` precedent:

- `GraphAlreadyCompleteException(string graphId, string runId)` — maps to 409 `urn:vais-agents:graph-already-complete`.
- `GraphInterruptMismatchException(string graphId, string runId, string suppliedInterruptId, string? pendingInterruptId)` — maps to 409 `urn:vais-agents:graph-interrupt-mismatch`.
- `GraphRunNotFoundException(string graphId, string runId)` — maps to 404 `urn:vais-agents:graph-run-not-found`.
- `GraphRunConflictException(string graphId, string runId)` — maps to 409 `urn:vais-agents:graph-run-conflict`.
- `GraphHandleNotFoundException(string graphId, string? version)` — maps to 404 `urn:vais-agents:graph-handle-not-found` (for invoke on a non-existent graph).

Existing `GraphRecursionException` (v0.9, Abstractions) — maps to 422 `urn:vais-agents:graph-recursion-limit`. No code change; just a row in `ProblemDetailsMapping`.

---

## Problem-Details URN catalogue — v0.19 additions

| URN | HTTP | Source exception | Meaning |
|---|---|---|---|
| `urn:vais-agents:graph-conflict` | 409 | existing `InvalidOperationException` (Create on existing `(id, version)`) | A graph with the same id+version already exists. |
| `urn:vais-agents:graph-handle-not-found` | 404 | `GraphHandleNotFoundException` | Graph id (and optional version) not registered. |
| `urn:vais-agents:graph-run-not-found` | 404 | `GraphRunNotFoundException` | No run or checkpoint found for the given `(graphId, runId)`. |
| `urn:vais-agents:graph-run-conflict` | 409 | `GraphRunConflictException` | Caller supplied a `RunId` that collides with an active run. |
| `urn:vais-agents:graph-interrupt-mismatch` | 409 | `GraphInterruptMismatchException` | Resume request's `InterruptId` does not match the checkpoint's `PendingInterruptId`. |
| `urn:vais-agents:graph-already-complete` | 409 | `GraphAlreadyCompleteException` | Resume target has `IsComplete == true`. |
| `urn:vais-agents:graph-recursion-limit` | 422 | `GraphRecursionException` (v0.9) | Run exceeded `MaxSteps`. |
| `urn:vais-agents:graph-validation-failed` | 422 | `AgentManifestValidationException` (existing; graph path adds rows) | Graph manifest failed validation (schema / edge-target-missing / entry-not-in-nodes). |

---

## Open items the pillar plan must answer

- **Streaming-SSE Problem-Details on interrupt.** The SSE transport emits a `GraphInterrupted` event (happy path). When the *response stream itself* fails mid-run, is there a pattern we reuse from v0.12? Confirm v0.12 approach for `GraphFailed` + 503 tail events.
- **OPA policy hook.** `Control.Policy.Opa` already wires per-verb on Agents. For graphs, we gate on the 7 `GraphXxx` `PolicyOperation` values. Default rego (`vais-agents.policy`) needs a graph-shape sample in a docs update.
- **Langfuse enrichment on graph events.** v0.19 does not extend enrichment; the existing `AddAgenticOpenTelemetrySink` consumes `AgentEvent` — the graph-event bus is separate. Pillar F polish.
- **Helm RBAC.** `AgentGraph` CRD needs its own `ClusterRole` rules. Mirror the `Agent` rules 1-for-1 in the Helm chart's RBAC template.

---

## Next

Pillar plan (PR-by-PR task lists) follows in `plans/actor-agents-oss-v0.19-graph-as-deployable-pillar.md`. Tag target: `v0.19.0-preview` on the runtime-host + docs commit of PR 4, per the two-commit precedent from v0.17 / v0.18.
