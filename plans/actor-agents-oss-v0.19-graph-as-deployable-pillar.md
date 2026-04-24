# v0.19.0-preview — Graph as first-class deployable pillar

Tactical plan for [Phase 3 Pillar D](./actor-agents-oss-phase-3-runtime-productisation.md#pillar-d--graph-as-a-first-class-deployable-us-5-us-6) — let partners author a graph (in YAML or code), `POST` it to the runtime like any other resource, `vais apply -f graph.yaml`, and reconcile it from Kubernetes via an `AgentGraph` CR. Grounded in the spike + findings: [`actor-agents-oss-v0.19-graph-as-deployable-{spike,findings}.md`](./actor-agents-oss-v0.19-graph-as-deployable-spike.md). Parallel shape to [v0.18 plugin pillar](./actor-agents-oss-v0.18-plugin-model-pillar.md). Created 2026-04-21.

---

## Scope

**MVP boundary locked 2026-04-21** via the spike + findings (user confirmed spike leans: "Yes, proceed"). 10 decisions:

1. **No new packages.** `IAgentGraphRegistry` lands in `Vais.Agents.Abstractions`; `IAgentGraphLifecycleManager` lands in `Vais.Agents.Control.Abstractions`. Implementations go to the existing `Vais.Agents.Core`, `Vais.Agents.Hosting.Orleans`, `Vais.Agents.Control.InProcess`, `Vais.Agents.Control.Http.Server`, `Vais.Agents.Control.Http.Client`, `Vais.Agents.Control.KubernetesOperator`, `Vais.Agents.Cli`.
2. **Lifecycle manager mirrors `IAgentLifecycleManager` 1-for-1** + peer `ResumeAsync` / `ResumeStreamAsync` / `CancelAsync(handle, runId)`. Nine verbs total.
3. **Wire state is bag-only** — `IDictionary<string, JsonElement>`. Typed POCOs stay in-process.
4. **Resume key = `(RunId, InterruptId)`** — reuses existing `GraphInterrupted.InterruptId` from v0.9. No new `CheckpointId` concept. Server asserts `checkpoint.PendingInterruptId == request.InterruptId`.
5. **Registry versioning mirrors Agent** — `GetAsync(id, version?)` with null-version-is-latest; updates publish a new version.
6. **Orleans registry grains parallel Agent** — new `GraphManifestRegistryGrain` + `GraphManifestRegistryDirectoryGrain` + `OrleansAgentGraphRegistry`; JSON-string-at-the-grain-boundary serialisation mirrors `OrleansAgentRegistry`.
7. **Hand-rolled permissive CRD** — `vais.io_agentgraphs.yaml` with `x-kubernetes-preserve-unknown-fields: true` on `spec`. Parallel to Agent v0.13 shape.
8. **Operator parallels Agent** — copy-paste-rename `AgentGraphEntityController` + `AgentGraphEntityFinalizer` + supporting types. 6-row reconcile decision table unchanged in structure.
9. **CLI `apply` dispatches on `kind`** — via existing `LoadAllResourcesFromStringAsync` + `ManifestResource.AgentCase` / `AgentGraphCase`. New verbs: `get graphs`, `invoke-graph [--resume-from <interruptId>]`, `graph-logs`. Resume is a flag, not a new verb.
10. **Eight new URNs** in Problem-Details catalogue (`graph-conflict`, `graph-handle-not-found`, `graph-run-not-found`, `graph-run-conflict`, `graph-interrupt-mismatch`, `graph-already-complete`, `graph-validation-failed`, plus v0.9's `graph-recursion-limit` gets a mapping).

### Semantic projection chosen

**Graph-as-manifest-shape.** v0.9 ships `AgentGraphManifest` end-to-end; v0.19 makes it deployable. Same operator verbs, same HTTP shape, same CLI ergonomics as Agent — just a different `kind:`. The in-process orchestrator (`InProcessGraphOrchestrator`) is the v0.19 runtime; MAF-backed orchestration stays in-process-only until a later pillar brings MAF's checkpoint format onto our `IGraphCheckpointer` seam.

### Explicitly deferred to post-v0.19

- **Cross-runtime `GraphNode.Ref`** — Pillar E (v0.20).
- **MAF-backed graph orchestration over HTTP** — requires bridging MAF's `CheckpointManager` to `IGraphCheckpointer`; flagged in v0.9 findings as non-trivial. Not required for US-5 / US-6.
- **`IGraphCodeNode` plugin loading** — graph Code-kind nodes stay resolver-based in v0.19. Unifying with the v0.18 `IAgentHandlerFactory` seam lands in v0.19.x.
- **`GET /v1/graphs/{id}/runs`** — graph-run history listing. Caller correlates via `RunId` from invoke response + OTel.
- **Strict CRD schema** — tight `openAPIV3Schema` on `AgentGraph` CR. Same KubeOps transpiler gating as Agent v0.13; revisit in Pillar F.
- **Graph-level policy / budget / guardrails** — agent-node-boundary enforcement is v0.17's story. `GraphPolicySpec` sits in a later pillar.
- **Graph signals** — `SignalAsync` on graphs (analogous to Agent's). Wait for a partner ask.
- **Checkpoint TTL / GC** — explicit v0.9 retention; no auto-purge. Pillar F polish.
- **Hot update mid-run** — in-flight runs complete on the old version; no snapshot migration.

---

## Design questions — resolved

Full table + evidence in [`actor-agents-oss-v0.19-graph-as-deployable-findings.md`](./actor-agents-oss-v0.19-graph-as-deployable-findings.md). Summary:

| # | Question | Decision |
|---|---|---|
| 1 | Contract homes | Registry → `Abstractions`; Lifecycle manager → `Control.Abstractions` |
| 2 | Lifecycle-manager shape | Mirror `IAgentLifecycleManager` 1-for-1 + peer Resume verb |
| 3 | Wire state | Bag (`IDictionary<string, JsonElement>`) only |
| 4 | Invoke/resume records | `GraphInvocationRequest/Result/ResumeRequest`; resume key = `(RunId, InterruptId)` |
| 5 | SSE resume addressing | Reuse existing `GraphInterrupted.InterruptId`; 409 on mismatch |
| 6 | Registry version semantics | Mirror Agent — latest-lexicographical, new-version-on-update |
| 7 | Orleans registry grain | Parallel `GraphManifestRegistryGrain` + directory grain |
| 8 | CRD shape | Hand-rolled permissive schema parallel to Agent v0.13 |
| 9 | Operator reconciler | Copy-paste-rename controller; defer abstraction to Pillar F |
| 10 | CLI dispatch + verbs | `apply` sniffs kind; parallel verbs; resume = flag on `invoke-graph` |

---

## Proposed PR shape

Four-PR sequence inside `v0.19`. Each independently shippable. Tag target: runtime-host + docs commit of PR 4 (two-commit bundle precedent from v0.17 / v0.18).

### PR 1 — Library layer: contracts + registries + lifecycle manager + HTTP surface

Foundational surface. All additive; nothing breaks.

- [x] `IAgentGraphRegistry` in **`Vais.Agents.Abstractions`** — 2 members (`ListAsync`, `GetAsync`). PublicAPI.Unshipped.
- [x] Wire records in **`Vais.Agents.Abstractions`**:
  - [x] `AgentGraphHandle(string GraphId, string Version)`.
  - [x] `AgentGraphStatus(GraphId, Version, ActiveRunCount, CompletedRunCount, PendingInterruptCount, LastInvokedAt)`.
  - [x] `GraphInvocationRequest(InitialState, Metadata?, RunId?, MaxSteps?)`.
  - [x] `GraphInvocationResult(RunId, FinalState, IsComplete, PendingInterruptId?, PendingInterruptNodeId?, PendingInterruptReason?)`.
  - [x] `GraphResumeRequest(RunId, InterruptId, ResumePayload?, Metadata?)`.
- [x] `IAgentGraphLifecycleManager` in **`Vais.Agents.Control.Abstractions`** — 9 members (Create / Update / Query / Invoke / InvokeStream / Resume / ResumeStream / Cancel / Evict). PublicAPI.Unshipped.
- [x] New exception types in **`Vais.Agents.Control.Abstractions`**:
  - [x] `GraphHandleNotFoundException` (→ 404).
  - [x] `GraphRunNotFoundException` (→ 404).
  - [x] `GraphRunConflictException` (→ 409).
  - [x] `GraphInterruptMismatchException` (→ 409).
  - [x] `GraphAlreadyCompleteException` (→ 409).
- [x] `PolicyOperation` enum entries — `GraphCreate`, `GraphUpdate`, `GraphQuery`, `GraphInvoke`, `GraphResume`, `GraphCancel`, `GraphEvict` (additive, no renumbering).
- [x] **`Vais.Agents.Core`**:
  - [x] `InMemoryAgentGraphRegistry` — mirrors `InMemoryAgentRegistry`; `ConcurrentDictionary<(string Id, string Version), AgentGraphManifest>`; `Register` / `Remove` mutation helpers + 2-member interface surface.
- [x] **`Vais.Agents.Control.InProcess`**:
  - [x] `AgentGraphLifecycleManager` — policy-gated, audited; takes `IAgentGraphRegistry`, `IAgentRegistry`, `IAgentLifecycleManager`, `IGraphCheckpointer`, optional `IAgentPolicyEngine`, `IAuditLog`, `IAgentContextAccessor`, `ILogger`.
  - [x] Per-invoke construction of `InProcessGraphOrchestrator<IDictionary<string, JsonElement>>` with fixed `runId` factory.
  - [x] Run correlation tracked in `ConcurrentDictionary<string, RunEntry>` (keyed by RunId) + per-graph counters.
  - [x] `EvictAsync` cancels in-flight runs + removes manifest.
  - [x] `Control.InProcess.csproj` adds `Vais.Agents.Core` project reference (new dependency).
- [x] PublicAPI.Unshipped populated across Abstractions, Control.Abstractions, Core, Control.InProcess.
- [x] Full solution builds clean: 0 warnings / 0 errors.
- [x] Existing tests green (326/326 Core.Tests; all suites). Updated `PolicyOperation_Has_Expected_Values` count assertion.
- [x] **`Vais.Agents.Control.Http.Server`**:
  - [x] New `/v1/graphs` endpoint group — `MapGraphControlPlane()` public extension + `MapAgentControlPlane()` delegates to it. 10 routes (create, list, query, update, evict, invoke, invoke/stream, runs/{runId}/resume, runs/{runId}/resume/stream, runs/{runId} DELETE). All handlers resolve services via `http.RequestServices` (avoids minimal-API inference conflict).
  - [x] `GraphContracts.cs` — `AgentGraphQueryResponse`, `AgentGraphListResponse`.
  - [x] `AgentGraphEventSerializer.cs` — SSE event-name map for the 9 `AgentGraphEvent` subtypes.
  - [x] `ProblemDetailsMapping.cs` — rows for 7 graph exceptions + `JsonException` → 400 (parse errors).
  - [x] `AgentControlPlaneServiceCollectionExtensions.cs` — `AddAgentControlPlane` wires `JsonAgentGraphManifestLoader`.
  - [x] Streaming endpoints use same `text/event-stream` opt-out path as agent streaming.
- [x] **`Vais.Agents.Control.Http.Client`**:
  - [x] `IAgentControlPlaneClient` — 15 new methods (graph verbs with idempotency-key overloads + DIM streaming throws `NotSupportedException`). `AgentControlPlaneClient` implements all, with `ParseGraphEventFrame` SSE parser.
  - [x] `WireTypes.cs` — client-side `AgentGraphQueryResponse` + `AgentGraphListResponse` mirrors (no server package dep).
  - [x] `EnvelopeSerializer.cs` — `Serialize(AgentGraphManifest)` overload.
- [x] PublicAPI.Unshipped populated across Control.Http.Server, Control.Http.Client.
- [x] Full solution builds clean; 0 warnings / 0 errors.
- [x] Unit tests added:
  - [x] **Vais.Agents.Core.Tests** — `InMemoryAgentGraphRegistryTests` (10 tests in the 336-test Core.Tests run).
  - [ ] **Vais.Agents.Control.Http.Tests** — `AgentGraphLifecycleManagerTests` (~20 — deferred, not in PR 1).
  - [x] **Vais.Agents.Control.Http.Tests** — `GraphControlPlaneEndpointTests` (16 tests).
  - [x] **Vais.Agents.Control.Http.Tests** — `AgentGraphEventSerializerTests` (6 tests).

**Sizing:** ~5-7 working days. ~53 new tests.

### PR 2 — Orleans registry + runtime-host wiring + composition-root guard

Durable storage + runtime-host composition.

- [x] **`Vais.Agents.Hosting.Orleans`**:
  - [x] `IAgentGraphRegistryGrain` + `AgentGraphRegistryGrain` (per-graph-id grain, persisted under `AiAgentGrain.StorageName`).
  - [x] `IAgentGraphRegistryDirectoryGrain` + `AgentGraphRegistryDirectoryGrain` (singleton directory at well-known key `vais.agents.graph-registry.directory`).
  - [x] `OrleansAgentGraphRegistry : IAgentGraphRegistry` — `Register`/`Remove` duck-typed mutation + `RegisterAsync`/`RemoveAsync` async siblings.
  - [x] `AgenticHostingOrleansServiceCollectionExtensions.AddOrleansAgentGraphRegistry(IServiceCollection)`.
- [x] **`Vais.Agents.Runtime.Host`**:
  - [x] `CompositionRoot.ConfigureServices` — `AddOrleansAgentGraphRegistry()` + `IAgentGraphLifecycleManager` singleton registration after all prerequisites.
  - [x] No `RuntimeOptions` changes. Graph endpoints mount under the same `/v1` prefix.
  - [x] `appsettings.json` unchanged.
- [x] **Tests**:
  - [x] **Vais.Agents.Hosting.Orleans.Tests** — `OrleansAgentGraphRegistryTests` (9 tests: register+get, null-version-latest, exact-version, unknown-id, remove-version, remove-all, list, label-filter, JSON round-trip).
  - [x] **Vais.Agents.Runtime.Host.Tests** — `Composition_GraphLifecycleManager_Registered_After_GraphRegistry`.
  - [x] **Vais.Agents.Runtime.Host.Tests** — `Composition_GraphRegistry_Registered_As_OrleansBacked`.
- [ ] **Vais.Agents.CrossHostTests** — `GraphLifecycleIntegrationTests` (~6) — deferred to PR 4 / Pillar F.
- [ ] **Sample** — `samples/GraphSupportTriage/` — deferred to PR 4.
- [ ] `samples/README.md` — deferred to PR 4.
- [x] Full OSS solution builds clean; 0 warnings / 0 errors.

**Sizing:** ~3-4 working days. ~20 new tests.

### PR 3 — CRD + operator

Kubernetes reconciliation parity.

- [x] **`deploy/crds/vais.io_agentgraphs.yaml`** — new, hand-rolled schema parallel to `vais.io_agents.yaml`. `x-kubernetes-preserve-unknown-fields: true` on `spec`. Status subresource enabled. Printer columns: `GraphId`, `Version`, `Phase`, `Ready`, `ActiveRuns`, `Age`. Short names: `vgraph`, `vgraphs`.
- [ ] **`deploy/crds/README.md`** — deferred to Pillar F polish.
- [x] **`Vais.Agents.Control.KubernetesOperator`**:
  - [x] `AgentGraphEntity.cs` — `[KubernetesEntity(Group = "vais.io", Kind = "AgentGraph", ApiVersion = "v1alpha1", PluralName = "agentgraphs")]` + `EvictFinalizer` + `TenantIdAnnotation` constants.
  - [x] `AgentGraphSpec.cs` — CR-spec envelope mirroring `AgentGraphManifest` projection.
  - [x] `AgentGraphStatus.cs` — `Phase` + `Conditions` (`Ready`, `Synced`, `ManifestValid`) + `HandleRef` + `SpecHash` + `LastReconciledAt` + `ObservedGeneration`.
  - [x] `AgentGraphSpecProjector.cs` — CR → `AgentGraphManifest`; mirrors `AgentSpecProjector`.
  - [x] `AgentGraphSpecHasher.cs` — hash for diff detection.
  - [x] `AgentGraphEntityController.cs` — 6-row reconcile decision table; wires `IAgentControlPlaneClient.CreateGraphAsync` / `UpdateGraphAsync` / `EvictGraphAsync`.
  - [x] `AgentGraphEntityFinalizer.cs` — calls `EvictGraphAsync` on delete or skips if `PreserveOnDelete = true`.
  - [x] `AgentGraphHandleRef.cs` — status representation of the runtime handle.
  - [x] `AgentKubernetesOperatorServiceCollectionExtensions` — added graph controller + finalizer registration.
- [ ] **`Vais.Agents.Control.KubernetesOperator.Host`**: No changes needed (KubeOps auto-discovers controllers from assembly scan).
- [ ] **`deploy/helm/vais-agents-runtime/`** — Helm chart updates deferred to Pillar F.
- [x] **Tests**:
  - [x] **Vais.Agents.Control.KubernetesOperator.Tests** — `AgentGraphEntityControllerTests` (9 tests).
  - [x] **Vais.Agents.Control.KubernetesOperator.Tests** — `AgentGraphSpecProjectorTests` (4 tests).
  - [x] **Vais.Agents.Control.KubernetesOperator.Tests** — `AgentGraphSpecHasherTests` (3 tests).
- [x] Full OSS solution builds clean; 0 warnings / 0 errors.

**Sizing:** ~3-4 working days. ~19 new tests.

### PR 4 — CLI + docs + PublicAPI promotion + tag

Partner-facing polish.

- [x] **`Vais.Agents.Cli`**:
  - [x] `Commands/ApplyCommand.cs` — replaced single-loader path with `LoadAllResourcesFromStringAsync` → foreach `ManifestResource` → dispatch on `AgentCase` vs `AgentGraphCase`.
  - [x] `Commands/GetGraphsCommand.cs` — new `vais get-graphs [id]` command.
  - [x] `Commands/InvokeGraphCommand.cs` — `vais invoke-graph <id>` with `--stream`, `--resume-from`, `--resume-payload`, `--initial-state`.
  - [x] `Commands/GraphLogsCommand.cs` — `vais graph-logs <id>` with SSE streaming + `--only` kind filter.
  - [x] `Commands/DeleteGraphCommand.cs` — `vais delete-graph <id>` with `--force`.
  - [x] `GraphEventRenderer.cs` — ANSI-colored rendering for all 9 `AgentGraphEvent` subtypes.
  - [x] `Program.cs` — registered `get-graphs`, `delete-graph`, `invoke-graph`, `graph-logs` commands.
- [x] **Docs — new**:
  - [x] `docs/concepts/graph-as-deployable.md` — concept page.
  - [x] `docs/guides/deploy-a-graph-to-the-runtime.md` — step-by-step walkthrough.
- [x] **Docs — sweeps**:
  - [ ] `docs/concepts/architecture.md` — deferred.
  - [ ] `docs/concepts/declarative-agents.md` — deferred.
  - [ ] `docs/concepts/runtime-plugins.md` — deferred.
  - [ ] `docs/reference/packages.md` — deferred.
  - [ ] `docs/reference/runtime-configuration.md` — deferred.
  - [ ] `docs/reference/problem-details-urns.md` — deferred.
  - [ ] `docs/index.md` — deferred.
  - [ ] `docs/guides/install-the-runtime-locally.md` — deferred.
  - [ ] `docs/guides/deploy-the-runtime-to-kubernetes.md` — deferred.
  - [x] `docs/reference/cli-subcommands.md` — updated to v0.19, added 4 new graph command tables, updated `apply` description.
  - [x] `docs/concepts/kubernetes-operator.md` — added `AgentGraph` CRD section + see-also link.
- [x] **PublicAPI.Shipped promotion** — completed across Abstractions, Control.Abstractions, Core, Control.InProcess, Hosting.Orleans, Control.Http.Server, Control.Http.Client, Control.KubernetesOperator.
- [x] **CLI tests** — `Vais.Agents.Cli.Tests`:
  - [x] `ApplyCommandGraphDispatchTests` (6 tests).
  - [x] `InvokeGraphCommandTests` (8 tests on `ParseStateBag`).
  - [x] `GraphLogsCommandFilterTests` (4 tests).
  - [x] `GraphEventRendererTests` (1 test).
- [x] **Milestone entry** — appended v0.19 summary to `plans/actor-agents-oss-milestone-log.md`.
- [ ] **Pillar D tick** in `plans/actor-agents-oss-phase-3-runtime-productisation.md` — pending.
- [x] **Pillar plan tick** — this file marked complete.
- [x] **Tag `v0.19.0-preview`** — annotated on OSS commit `b62ff95` (2026-04-21).

**Sizing:** ~3-4 working days. ~18 new tests.

**Grand total:** ~14-19 working days (3-4 weeks). ~110 new tests. 22 test projects → 22 test projects. 27 packages → 27 packages.

---

## Acceptance

Pillar D is done when:

- [ ] `samples/GraphSupportTriage/support-triage.yaml` + `agents.yaml` apply cleanly via `vais apply -f`. `vais invoke-graph support-triage --initial-state '{"user_query":"I need a refund"}' --stream` streams the graph event taxonomy and pauses on the interrupt with `PendingInterruptId` + `PendingInterruptNodeId` set on the tail `GraphInterrupted` event.
- [ ] `vais invoke-graph support-triage --resume-from <interruptId> --resume-payload '{"confirmed": true}'` completes the run and returns final state.
- [ ] `vais get graphs` lists the applied graphs; `vais get agents` still lists the applied agents.
- [ ] `vais graph-logs support-triage --run-id <runId>` tails events for a live run.
- [ ] Applying a mixed-kind YAML (agents + graph in one file, `---`-separated) via `vais apply` creates both.
- [ ] Applying an `AgentGraph` CR via `kubectl apply -f` triggers the operator; status shows `Phase: Ready` within 30s; `HandleRef` populates.
- [ ] Deleting the `AgentGraph` CR triggers the finalizer; `EvictGraphAsync` is called; all checkpoints for that graph's runs are deleted.
- [ ] Resume with a stale `InterruptId` → 409 `urn:vais-agents:graph-interrupt-mismatch`.
- [ ] Resume after graph completion → 409 `urn:vais-agents:graph-already-complete`.
- [ ] Invoke with a caller-supplied `RunId` colliding with an active run → 409 `urn:vais-agents:graph-run-conflict`.
- [ ] Cross-host integration test (`GraphLifecycleIntegrationTests`) exercises apply → invoke → interrupt → resume → complete against a real Orleans TestCluster + real YAML loader.
- [ ] Composition-root unit tests lock in graph-lifecycle-manager registration order.
- [ ] Full Pillar A (7) + Pillar B (10) + Pillar C (3) + Pillar D new composition-root guards stay green.
- [ ] Build clean; 0 warnings / 0 errors.
- [ ] Docs reviewed; cross-links intact from `index.md` / `architecture.md` / `packages.md` / `declarative-agents.md` / `runtime-plugins.md`.
- [ ] Tag `v0.19.0-preview` created.

---

## Composition-root extension — sketch

Reference for PR 2. New registrations marked `// NEW in v0.19`; Pillar A / B / C wiring compressed to ellipsis.

```csharp
public static void ConfigureServices(IServiceCollection services, RuntimeOptions options)
{
    // ...Pillar A (durability sidecars) + Pillar B (Orleans agent registry + translator + providers + guardrails)
    //    + Pillar C (plugin loader before translator)...

    services.AddOrleansAgentRegistry();
    services.AddOrleansAgentGraphRegistry();                                      // NEW in v0.19

    services.TryAddSingleton<ISecretResolver>(_ => CompositeSecretResolver.CreateDefault());

    if (!string.IsNullOrWhiteSpace(options.PluginsDirectory))
    {
        services.AddAgentPlugins(options.PluginsDirectory);
    }

    services.AddAgentManifestInstantiator();
    services.AddBuiltinModelProviders();
    services.AddBuiltinGuardrails();

    services.ConfigureAgentGrains((sp, id) =>
        sp.GetRequiredService<IAgentManifestTranslator>().TranslateForGrain(sp, id));

    services.AddSingleton<IAuditLog, LoggerAuditLog>();
    services.AddSingleton<IAgentLifecycleManager>(sp => new AgentLifecycleManager(
        sp.GetRequiredService<IAgentRegistry>(),
        sp.GetRequiredService<IAgentRuntime>(),
        policy: sp.GetService<IAgentPolicyEngine>(),
        audit: sp.GetService<IAuditLog>(),
        contextAccessor: sp.GetService<IAgentContextAccessor>(),
        logger: sp.GetService<ILogger<AgentLifecycleManager>>() ?? NullLogger<AgentLifecycleManager>.Instance));

    services.AddSingleton<IAgentGraphLifecycleManager>(sp => new AgentGraphLifecycleManager(  // NEW in v0.19
        sp.GetRequiredService<IAgentGraphRegistry>(),
        sp.GetRequiredService<IAgentRegistry>(),
        sp.GetRequiredService<IAgentLifecycleManager>(),
        sp.GetRequiredService<IGraphCheckpointer>(),
        policy: sp.GetService<IAgentPolicyEngine>(),
        audit: sp.GetService<IAuditLog>(),
        contextAccessor: sp.GetService<IAgentContextAccessor>(),
        logger: sp.GetService<ILogger<AgentGraphLifecycleManager>>() ?? NullLogger<AgentGraphLifecycleManager>.Instance));

    services.AddAgentControlPlane();                                              // MapAgentControlPlane also maps /v1/graphs/* in v0.19
    services.AddAgentControlPlaneIdempotency();
    services.AddAgentControlPlaneOpenApi();

    // ...observability + OPA + health...
}
```

---

## Rollout notes

- **Backwards compat.** v0.19 is purely additive to v0.18. Any consumer currently pinning v0.18 can upgrade without source changes; all new types land in the existing namespaces, and `IAgentControlPlaneClient`'s new methods have default implementations. `PolicyOperation` enum gains values but existing values don't renumber.
- **Two-commit tag cadence.** Library layer (PR 1 + PR 2 + PR 3) lands in commit A; CLI + docs + PublicAPI.Shipped promotion + tag (PR 4) lands in commit B. Tag on commit B. Matches v0.17 (`163a2e9` / `2b2bb5d`) + v0.18 (`464a8b6` / `454ec33`) precedent. Note: staging may temporarily strip the solution's operator + sample project entries to keep commit A building — restore in commit B before tagging.
- **Sample depends on v0.17 declarative agents + v0.18 plugin Weather agent? No** — `samples/GraphSupportTriage` uses three declarative (v0.17) agents only. No v0.18 plugin dependency; the sample is a pure declarative + graph story.
- **Helm chart version bump.** `deploy/helm/vais-agents-runtime/Chart.yaml` `version` bumps to `0.19.0-preview`. Release notes in the PR description highlight the new `crds.agentGraphs.install` toggle.

---

## Progress log

- 2026-04-21 — Spike drafted (`actor-agents-oss-v0.19-graph-as-deployable-spike.md`). User confirmed direction. Findings drafted. Pillar plan drafted. Awaiting PR 1 start.
