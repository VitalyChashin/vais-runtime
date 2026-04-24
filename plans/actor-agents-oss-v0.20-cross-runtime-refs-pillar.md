# v0.20.0-preview — Cross-runtime graph refs pillar

Tactical plan for [Phase 3 Pillar E](./actor-agents-oss-phase-3-runtime-productisation.md#pillar-e--cross-runtime-graph-refs-us-7-remote-case) — let a graph node reference an agent deployed on a different runtime instance so US-7 ("graph re-uses already-deployed agents on a different runtime") is fully satisfied. Grounded in the spike + findings: [`actor-agents-oss-v0.20-cross-runtime-refs-{spike,findings}.md`](./actor-agents-oss-v0.20-cross-runtime-refs-spike.md). Parallel shape to [v0.19 pillar](./actor-agents-oss-v0.19-graph-as-deployable-pillar.md). Created 2026-04-21.

---

## Scope

**MVP boundary locked 2026-04-21** via the spike + findings. 10 decisions:

1. **`GraphAgentRef` grows one additive field.** `record GraphAgentRef(string Id, string? Version = null, string? RuntimeUrl = null)`. Null = local (as today). No `GraphNode` shape change.
2. **`IAgentRemoteInvoker` — thin new interface** in `Vais.Agents.Control.Abstractions` with `InvokeAsync` + `StreamAsync`. `HttpAgentRemoteInvoker` in `Vais.Agents.Control.Http.Client` implements it by delegating to one `IAgentControlPlaneClient` per unique `RuntimeUrl`.
3. **One branch per orchestrator.** `InProcessGraphOrchestrator.ExecuteNodeAsync` checks `node.Ref?.RuntimeUrl != null`. `GraphNodeExecutor` (MAF) gets the identical branch. No new orchestrator abstraction.
4. **Bearer forwarding** — `IHttpContextAccessor.HttpContext?.Request.Headers.Authorization` forwarded verbatim to `IAgentRemoteInvoker`. Zero config. Configurable propagation deferred to v0.21.
5. **`RemoteAgentInvocationException`** — new exception in `Vais.Agents.Core` with `RuntimeUrl`, `Status` (HttpStatusCode), `IsRetryable`. 404 → non-retryable. 503 / 504 / 429 → retryable via Polly.
6. **Polly retry** — `HttpAgentRemoteInvoker` uses the same `HttpPolicyOptions` already in place for `IAgentControlPlaneClient` (retry + circuit breaker). No new retry configuration surface.
7. **Manifest loader** — `JsonAgentGraphManifestLoader.ParseNodes` gains additive `runtimeUrl` field with `http`/`https` URI validation. YAML loader delegates to JSON loader (no separate work). K8s CRD projector adds `RuntimeUrl = spec.RuntimeUrl`.
8. **CRD schema** — `deploy/crds/vais.io_agentgraphs.yaml` gains `runtimeUrl: { type: string }` under `spec.nodes.items.properties.ref`. No breaking schema change; old CRs round-trip unchanged.
9. **Null version on remote** — pass through to remote runtime (remote resolves latest). No extra round-trip. Documented in manifest format spec.
10. **A2A deferred.** No `A2AUrl` field on `GraphAgentRef` in v0.20. A2A-as-tool is the documented workaround.

### Explicitly deferred to post-v0.20

- `A2AUrl` on `GraphAgentRef` — v0.21 (alongside A2A structured output pillar).
- Configurable identity propagation (`RemoteRuntimeOptions`, OIDC token exchange) — v0.21 security hardening.
- Per-`RuntimeUrl` Polly option overrides — deferred; global `HttpPolicyOptions` sufficient for v0.20.
- Runtime topology discovery / `vais get-remote-runtimes` — Pillar F or later.
- `AgentGraph` cross-runtime invoke streaming — `IAgentRemoteInvoker.StreamAsync` is implemented but graph-level `InvokeStreamAsync` cross-remote path follows in a follow-up (same PR if time allows).

---

## Design questions — resolved

Full table + evidence in [`actor-agents-oss-v0.20-cross-runtime-refs-findings.md`](./actor-agents-oss-v0.20-cross-runtime-refs-findings.md). Summary:

| # | Question | Decision |
|---|---|---|
| Q1 | Discovery vs explicit config | Explicit-config (`RuntimeUrl` in `GraphAgentRef`) |
| Q2 | Where does `RuntimeUrl` live? | `GraphAgentRef` directly (additive, null = local) |
| Q3 | Transport | `IAgentRemoteInvoker` shim over `IAgentControlPlaneClient` |
| Q4 | Orchestrator changes | One branch in `ExecuteNodeAsync` per orchestrator |
| Q5 | Null version on remote | Pass through to remote runtime |
| Q6 | Identity propagation | Bearer forwarding (v0.20); configurable deferred |
| Q7 | Failure modes | `RemoteAgentInvocationException` + Polly retry |
| Q8 | MAF parity | Both orchestrators in v0.20 |
| Q9 | Manifest loader | Additive `runtimeUrl` field, URI validation |
| Q10 | A2A field | Defer to v0.21 |

---

## Proposed PR shape

Three-PR sequence inside `v0.20`. Tag target: docs + PublicAPI commit of PR 3 (same two-commit bundle pattern from v0.17/v0.18/v0.19).

### PR 1 — Core: `GraphAgentRef` extension + `RemoteAgentInvocationException` + `IAgentRemoteInvoker` + `HttpAgentRemoteInvoker` + orchestrator branches

All the runtime machinery. Additive; existing tests stay green.

**`Vais.Agents.Core`**

- [x] `GraphAgentRef` — add `string? RuntimeUrl = null` as third positional (default null). Existing call-sites constructing with 1 or 2 positionals unaffected.
- [x] `RemoteAgentInvocationException` — new `sealed class` extending `AgentInvocationException`:
  - Properties: `RuntimeUrl string`, `Status HttpStatusCode`, `IsRetryable bool`.
  - `IsRetryable` → true when `Status` is 503, 504, or 429.
- [x] PublicAPI.Unshipped entries for both.
- [x] `InProcessGraphOrchestrator.ExecuteNodeAsync` — add remote branch (≤ 12 lines):
  - `if (node.Ref?.RuntimeUrl is { } runtimeUrl)` → call injected `IAgentRemoteInvoker.InvokeAsync`.
  - Extract bearer token from `IHttpContextAccessor` (null-safe; no accessor = null token).
  - Map `RemoteAgentInvocationException` onto existing phase-error path identical to local failures.
  - Inject `IAgentRemoteInvoker` as optional ctor param (nullable; null → remote path throws `InvalidOperationException` with hint to register the service).
- [x] `InProcessGraphOrchestrator` MAF `GraphNodeExecutor.cs` — same branch (identical logic, different type hierarchy).

**`Vais.Agents.Control.Abstractions`**

- [x] `IAgentRemoteInvoker` — interface with two members:
  ```csharp
  ValueTask<InvocationResult> InvokeAsync(
      string runtimeUrl, AgentHandle handle, InvocationRequest request,
      string? bearerToken, CancellationToken cancellationToken = default);

  IAsyncEnumerable<InvocationStreamChunk> StreamAsync(
      string runtimeUrl, AgentHandle handle, InvocationRequest request,
      string? bearerToken, CancellationToken cancellationToken = default);
  ```
- [x] PublicAPI.Unshipped entry.

**`Vais.Agents.Control.Http.Client`**

- [x] `HttpAgentRemoteInvoker : IAgentRemoteInvoker` — `internal sealed` impl:
  - `ConcurrentDictionary<string, IAgentControlPlaneClient>` keyed by normalised `runtimeUrl` (scheme+host+port+basepath, lowercase).
  - Factory lambda creates an `AgentControlPlaneClient` backed by an `HttpClient` constructed via `IHttpClientFactory` with named client `vais.remote.{runtimeUrl}` (or direct ctor if factory unavailable).
  - Injects `Authorization: Bearer {bearerToken}` via a per-call `DelegatingHandler` (not mutating the shared `HttpClient`).
  - 503 / 504 / 429 → wraps as retryable `RemoteAgentInvocationException`; 404 → non-retryable.
  - Polly retry from `HttpPolicyOptions` (same as `AgentControlPlaneClient`).
- [x] `HttpAgentRemoteInvokerServiceCollectionExtensions.AddAgentRemoteInvoker(IServiceCollection)` — registers `HttpAgentRemoteInvoker` as `IAgentRemoteInvoker` singleton.
- [x] PublicAPI.Unshipped entries for the public DI extension.

**`Vais.Agents.Runtime.Host`**

- [x] `CompositionRoot.ConfigureServices` — call `services.AddAgentRemoteInvoker()` after control-plane wiring.
- [x] Runtime.Host.Tests — `Composition_RemoteInvoker_Registered` guard test.

**Tests**

- [x] `Vais.Agents.Core.Tests` — `RemoteAgentInvocationExceptionTests` (5 tests: ctor round-trip, IsRetryable for 503/504/429/404/500).
- [x] `Vais.Agents.Core.Tests` — `GraphAgentRef_RuntimeUrl_NullByDefault` + `GraphAgentRef_WithRuntimeUrl_Additive` (2 tests, guard for the ctor change).
- [x] `Vais.Agents.Control.Http.Tests` — `HttpAgentRemoteInvokerTests` (~10 tests: success round-trip, 404 non-retryable exception, 503 retryable exception, bearer header forwarding, null bearer OK, URL normalisation, client-per-URL pool).
- [x] `Vais.Agents.Core.Tests` — `InProcessGraphOrchestrator_RemoteBranchTests` (~8 tests: remote ref dispatched to invoker, local ref unchanged, null RuntimeUrl → local, missing invoker → InvalidOperationException, failure → phase Error, bearer forwarded).
- [x] Full solution builds clean; 0 warnings / 0 errors.

**Sizing:** ~4-5 working days. ~25 new tests.

---

### PR 2 — Manifest loader + YAML + CRD projector + K8s schema

Schema propagation. All additive.

**`Vais.Agents.Control.Manifests.Json`**

- [x] `JsonAgentGraphManifestLoader.ParseNodes` — parse optional `runtimeUrl` string from each node's `ref` object.
- [x] URI validation: `Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"` → else throw `AgentManifestValidationException` with message `"node '{id}': runtimeUrl must be an absolute http/https URI"`.
- [x] Wire into `GraphAgentRef(Id, Version, RuntimeUrl)`.

**`Vais.Agents.Control.Manifests.Yaml`**

- [x] No change needed — YAML loader delegates to JSON loader via `YamlToJson`. The `runtimeUrl` field in YAML round-trips through the existing pipeline automatically. One smoke test confirms it.

**`Vais.Agents.Control.KubernetesOperator`**

- [x] `AgentGraphSpec.cs` — no change needed; `Nodes` carries `IList<GraphNode>` which already contains `GraphAgentRef.RuntimeUrl` (projector copies nodes as-is).
- [x] `AgentGraphSpecProjector.cs` — no change needed; `spec.Nodes.ToList()` preserves `GraphAgentRef.RuntimeUrl` unchanged.
- [x] `deploy/crds/vais.io_agentgraphs.yaml` — add `runtimeUrl: { type: string, description: "Absolute http/https URL of the remote runtime hosting this agent. Null = local." }` under `spec.nodes.items.properties.ref`.

**Tests**

- [x] `AgentGraphManifestLoaderTests.Ref_RuntimeUrl_Parses_From_Json` — parses `runtimeUrl` from manifest JSON.
- [x] `AgentGraphManifestLoaderTests.Ref_InvalidRuntimeUrl_Scheme_Throws_Validation` — bad scheme → validation exception.
- [x] `AgentGraphManifestLoaderTests.Ref_RelativeRuntimeUrl_Throws_Validation` — relative URL → validation exception.
- [x] `AgentGraphManifestLoaderTests.Ref_Without_RuntimeUrl_Yields_Null` — existing manifests unchanged.
- [x] `AgentGraphManifestLoaderTests.Ref_RuntimeUrl_Parses_From_Yaml` — YAML with `runtimeUrl` round-trips.
- [x] `AgentGraphManifestLoaderTests.Envelope_RoundTrip_Preserves_RuntimeUrl` — envelope serializes and deserializes `runtimeUrl`.
- [x] `AgentGraphSpecProjectorTests.ToManifest_Preserves_RuntimeUrl_On_NodeRef` — CR `runtimeUrl` appears on `GraphAgentRef`.
- [x] Full solution builds clean; 0 warnings / 0 errors.

**Sizing:** ~2-3 working days. ~7 new tests.

---

### PR 3 — Docs + PublicAPI promotion + milestone + tag

Partner-facing polish.

**Docs — new**

- [x] `docs/concepts/cross-runtime-graphs.md` — concept page covering: when to use vs A2A-as-tool, `runtimeUrl` field + null-default, bearer forwarding + security, `RemoteAgentInvocationException` + retry, v0.20 limitations.
- [x] `docs/guides/compose-a-graph-across-runtimes.md` — step-by-step: two runtimes, apply enricher to B, write cross-pipeline manifest, apply to A, invoke + observe events, failure scenarios (404, 503, anonymous).

**Docs — sweeps**

- [x] `docs/concepts/graph-as-deployable.md` — added `runtimeUrl` field comment in manifest example + v0.20 feature row in pillar table + link to `cross-runtime-graphs.md`.
- [x] `docs/reference/cli-subcommands.md` — added cross-runtime note under `vais apply` documenting `--server` / `--context` usage for deploying remote agents.
- [x] `docs/concepts/architecture.md` — added cross-runtime refs callout under Graph orchestration section.
- [x] Other sweeps deferred to Pillar F (remaining docs from v0.19 deferred list).

**PublicAPI.Shipped promotion**

- [x] `Vais.Agents.Abstractions` — `IAgentRemoteInvoker` + `RemoteAgentInvocationException` promoted Unshipped → Shipped.
- [x] `Vais.Agents.Control.Http.Client` — `HttpAgentRemoteInvokerServiceCollectionExtensions` promoted.
- [x] Both Unshipped files reset to `#nullable enable`.

**Milestone housekeeping**

- [x] Append v0.20 entry to `plans/actor-agents-oss-milestone-log.md`.
- [x] Tick Pillar E in `plans/actor-agents-oss-phase-3-runtime-productisation.md`.
- [x] Mark this plan complete (PR tasks checked).
- [x] Tag `v0.20.0-preview` annotated on OSS main.

**Sizing:** ~2-3 working days. 0 new tests.

**Grand total:** ~8-11 working days (2 weeks). ~32 new tests. 27 packages → 27 packages (no new packages).

---

## Acceptance

Pillar E is done when:

- [ ] A graph manifest with `ref.runtimeUrl: https://runtime-b.svc` applied to runtime A routes node execution to runtime B. The `node.started` event carries `nodeId`; `node.completed` carries the result from the remote agent.
- [ ] A local-only graph (no `runtimeUrl`) continues to work identically — no regression.
- [ ] Remote 404 → `RemoteAgentInvocationException(Status: 404, IsRetryable: false)` → phase flips to `Error`; `graph.failed` event emitted with structured detail.
- [ ] Remote 503 → `HttpAgentRemoteInvoker` retries (Polly); eventual 503 after exhausting retries → `RemoteAgentInvocationException(Status: 503, IsRetryable: true)` → phase `Error`.
- [ ] Null `RuntimeUrl` + null `Version` on a node ref → remote runtime resolves "latest" version of that agent (pass-through).
- [ ] Bearer token from inbound request is forwarded on the remote call (observable via HTTP trace / test stub asserting `Authorization` header).
- [ ] MAF `GraphNodeExecutor` routes to remote invoker for `RuntimeUrl != null` nodes identically to `InProcessGraphOrchestrator`.
- [ ] Applying a cross-runtime YAML manifest (with `runtimeUrl`) parses cleanly; URI validation rejects `ftp://` or relative URLs with a descriptive validation error.
- [ ] YAML manifest with `runtimeUrl` round-trips through `YamlAgentGraphManifestLoader` correctly.
- [ ] K8s `AgentGraph` CR with `runtimeUrl` in a node ref projects through the operator spec projector into `GraphAgentRef.RuntimeUrl`.
- [ ] Composition-root guard test confirms `IAgentRemoteInvoker` resolves as `HttpAgentRemoteInvoker`.
- [ ] Full solution builds clean; 0 warnings / 0 errors.
- [ ] `docs/concepts/cross-runtime-graphs.md` + `docs/guides/compose-a-graph-across-runtimes.md` published and cross-linked.
- [ ] `PublicAPI.Shipped.txt` promoted for all affected assemblies.
- [ ] Tag `v0.20.0-preview` created.

---

## Composition-root extension — sketch

Reference for PR 1. New registration marked `// NEW in v0.20`; prior pillars compressed to ellipsis.

```csharp
public static void ConfigureServices(IServiceCollection services, RuntimeOptions options)
{
    // ... Pillar A durability sidecars ...
    // ... Pillar B Orleans registries + instantiator + providers + guardrails ...
    // ... Pillar C plugin loader ...
    // ... Pillar D graph registry + graph lifecycle manager ...

    services.AddAgentRemoteInvoker();                                              // NEW in v0.20

    services.AddSingleton<IAgentLifecycleManager>(sp => new AgentLifecycleManager(
        sp.GetRequiredService<IAgentRegistry>(),
        sp.GetRequiredService<IAgentRuntime>(),
        policy: sp.GetService<IAgentPolicyEngine>(),
        audit: sp.GetService<IAuditLog>(),
        contextAccessor: sp.GetService<IAgentContextAccessor>(),
        logger: sp.GetService<ILogger<AgentLifecycleManager>>() ?? NullLogger<AgentLifecycleManager>.Instance));

    services.AddSingleton<IAgentGraphLifecycleManager>(sp => new AgentGraphLifecycleManager(
        sp.GetRequiredService<IAgentGraphRegistry>(),
        sp.GetRequiredService<IAgentRegistry>(),
        sp.GetRequiredService<IAgentLifecycleManager>(),
        sp.GetRequiredService<IGraphCheckpointer>(),
        remoteInvoker: sp.GetService<IAgentRemoteInvoker>(),                       // NEW in v0.20
        policy: sp.GetService<IAgentPolicyEngine>(),
        audit: sp.GetService<IAuditLog>(),
        contextAccessor: sp.GetService<IAgentContextAccessor>(),
        logger: sp.GetService<ILogger<AgentGraphLifecycleManager>>() ?? NullLogger<AgentGraphLifecycleManager>.Instance));
}
```

**Ordering invariant:** `AddAgentRemoteInvoker()` must be called before the `IAgentGraphLifecycleManager` / `InProcessGraphOrchestrator` singletons are registered (so the `IAgentRemoteInvoker` service is resolvable when the orchestrator is first constructed).

---

## Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Bearer token not available in orchestrator (no `IHttpContextAccessor`) | Low | `IHttpContextAccessor` registered by `AddHttpContextAccessor()` which is already called in Pillar A's Runtime.Host; graph invoked outside HTTP request → null token → document limitation. |
| Remote runtime uses a different auth scheme (e.g., mTLS) | Medium | v0.20 only supports bearer forwarding; document explicitly. OIDC exchange / mTLS lands in v0.21 security pillar. |
| `HttpClient` pool exhaustion (many unique `RuntimeUrl`s) | Very low | `ConcurrentDictionary` client pool is bounded by distinct `runtimeUrl` values in registered manifests; typical use case is 1-3 runtimes. |
| Remote version drift (remote agent updated between graph nodes) | Low | Same-manifest invocation; `Version` is explicit. Null-version passes through; operator should pin versions in production. Document as a best practice. |
| CRD schema change requires manual `kubectl apply` on upgrade | Low | Change is additive (new optional field). No existing CR breaks. Standard Helm upgrade dance applies. |
