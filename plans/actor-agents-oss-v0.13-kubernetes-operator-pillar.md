# v0.13.0-preview — Kubernetes CRD + operator pillar

Tactical plan for the first Kubernetes-native pillar. Closes the [`extraction-research`](./actor-agents-oss-extraction-research.md) §7 backlog line: *"Kubernetes CRDs + operator (`Vais.Agents.Control.KubernetesOperator`) — declarative agents as native K8s resources; reconciler drives `IAgentLifecycleManager` verbs to match cluster state."* Grounded in the spike + findings: [`actor-agents-oss-v0.13-kubernetes-operator-spike.md`](./actor-agents-oss-v0.13-kubernetes-operator-spike.md) + [`actor-agents-oss-v0.13-kubernetes-operator-findings.md`](./actor-agents-oss-v0.13-kubernetes-operator-findings.md). Parallel shape to [`actor-agents-oss-v0.12-sse-streaming-invoke-pillar.md`](./actor-agents-oss-v0.12-sse-streaming-invoke-pillar.md). Created 2026-04-20.

---

## Scope

**MVP boundary locked 2026-04-20** via the research spike. 12 decisions:

1. **Single CRD — `Agent` only.** `AgentGraph` deferred to v0.14 (paired with `IAgentGraphRegistry` + `POST /v1/graphs` verbs); `AgentRun` deferred to v0.15 (paired with `IAgentRunRegistry` + `GET /v1/agents/{id}/runs/{runId}`). Single-CRD scope keeps the pillar honest against today's control-plane surface.
2. **CRD shape**: `[KubernetesEntity(Group = "vais.io", ApiVersion = "v1alpha1", Kind = "Agent")]` on `AgentEntity : CustomKubernetesEntity<AgentSpec, AgentStatus>`. Short names `vagent`, `vagents`. Namespaced.
3. **`AgentSpec`**: record mirroring `AgentManifest` field set (22 top-level fields + 15 nested record types) + `SecretRefs: IReadOnlyDictionary<string, SecretKeyReference>?` + `PreserveOnDelete: bool` (default `false`).
4. **`AgentStatus`**: `{ AgentHandle, ManifestRevision, Phase, LastReconciledAt, LastError, Conditions[], ObservedGeneration }`. Phase enum = `Pending | Creating | Active | Updating | Error | Terminating` (PascalCase on the wire; K8s idiom). Conditions = `Ready`, `Synced`, `ManifestValid`.
5. **Framework**: `KubeOps.Operator 9.x` metapackage. Targets `net9.0`. No hand-rolled controller loops.
6. **Project shape**: new library package `Vais.Agents.Control.KubernetesOperator` (CRD types + `AgentEntityController : IEntityController<AgentEntity>` + DI extensions). New in-repo-only projects: `Vais.Agents.Control.KubernetesOperator.Host` (runnable exe, Dockerfile source, not packaged) + `Vais.Agents.Control.KubernetesOperator.Tests`. Package count: **22 → 23**.
7. **Runtime wire**: operator → runtime over HTTP via `AgentControlPlaneClient`. No in-process co-host in v0.13. Stateless operator Deployment + silo cluster scale independently.
8. **Auth — operator side**: ServiceAccount-projected OIDC token (audience=`vais-agents-runtime`, TTL=3600s, path `/var/run/secrets/tokens/vais-runtime-token`). `ServiceAccountTokenHandler : DelegatingHandler` injects `Authorization: Bearer <token>` on every outbound `AgentControlPlaneClient` request. 5-min in-memory cache with file-mtime invalidation.
9. **Auth — runtime side**: unchanged from v0.6 — stock `AddJwtBearer` with `Authority=https://kubernetes.default.svc` + `Audience=vais-agents-runtime` + K8s API OIDC discovery for JWKS. Optional `ServiceAccountPrincipalMapper : IPrincipalMapper` shipped in the operator package for consumers who want SA namespace → `TenantId` mapping.
10. **Reconcile semantics**: hash-based diff (SHA-256 of canonical-JSON `spec`) + K8s status subresource + finalizer `vais.io/agent-deactivate` + 3 conditions + 6-state phase enum + `ObservedGeneration` pattern. Every operator → runtime call carries `Idempotency-Key = $"{uid}:{generation}:{verb}"` for v0.11 middleware dedup.
11. **Secret resolution**: `spec.secretRefs: { [logicalName] → { name, key } }`. Operator resolves via K8s API before `CreateAsync`/`UpdateAsync`; runtime receives plain values (same as env-resolver behaviour). `V1Secret` watch triggers re-reconcile on Secret changes → `UpdateAsync` propagates rotated keys. **Audit redaction dismissed** — `AuditLogEntry` records only verb metadata, never manifest body.
12. **Tenancy**: CRDs namespaced. Explicit `vais.io/tenant-id` annotation for tenant binding. Optional `ServiceAccountPrincipalMapper` maps SA namespace → `TenantId`. ClusterRole + ClusterRoleBinding by default (cluster-wide watch); production deployments can narrow via `WATCH_NAMESPACES`.

### Semantic projection chosen

**Declarative Agent CR as K8s-native manifest.** Users write `kubectl apply -f agent.yaml` with a CR whose `spec` mirrors `AgentManifest`. Operator reconciles → calls HTTP control plane → runtime materialises the agent. Exactly one CR ↔ exactly one runtime-registered agent. Operator holds no durable state of its own — K8s API + runtime state are sources of truth.

### Explicitly deferred to post-v0.13

- **`AgentGraph` CRD**. → v0.14 pillar. Requires `IAgentGraphRegistry` contract + `POST/GET/PATCH/DELETE /v1/graphs/{id}` HTTP verbs on the runtime side first.
- **`AgentRun` CRD**. → v0.15 pillar. Requires `GET /v1/agents/{id}/runs/{runId}` run-query endpoint + `IAgentRunRegistry` contract + TTL controller for finished runs.
- **Leader election / HA**. Single-replica MVP. `Microsoft.Extensions.Hosting` doesn't ship a built-in K8s Lease primitive; KubeOps 9.x has one but we don't wire it this pillar.
- **In-process co-hosted mode** (operator as `IHostedService` in the silo pod). Library leaves this door open (controller is DI-friendly) but we don't ship a co-host sample.
- **Automated kind-in-CI integration tests**. Controller logic covered by unit tests with mocked `IAgentControlPlaneClient` + `IKubernetesClient`. Cluster-side validation is a manual acceptance step against user's `docker-desktop` Kubernetes this round.
- **Public container image publishing**. Repo ships Dockerfile + Helm chart template; users `docker build + push` to their own registry. Public image pipeline is a separate release-automation concern.
- **CRD schema JSON shipped as standalone file**. CRD YAML + KubeOps transpiler sufficient; downstream consumers derive JSON-schema from CRD.
- **Multi-version CR support** (`v1alpha1` + `v1beta1`). Single `v1alpha1` version; storage-version upgrade is a future concern.
- **Custom operator metrics + traces**. KubeOps 9.x built-in metrics are enough for v0.13; OTel wiring is opt-in via operator host config.
- **Operator config hot-reload**. Config via env + CLI args; restart to reconfigure. Standard K8s operator pattern.
- **`optional: true` on `SecretKeyReference`**. Missing secret = reconcile error + condition + backoff. Consumers who want graceful-skip extend in a later version.
- **Audit-log redaction helpers**. `AuditLogEntry` doesn't capture manifest body — nothing to redact.

---

## Design questions — resolved

| # | Question | Decision | Reasoning |
|---|---|---|---|
| 1 | CRD schema design | `[KubernetesEntity]` + `CustomKubernetesEntity<AgentSpec, AgentStatus>` mirror of `AgentManifest` | Single source of truth; KubeOps transpiler emits OpenAPI from annotated records; `x-kubernetes-preserve-unknown-fields` on `JsonElement` sub-fields |
| 2 | Framework + host split | KubeOps 9.x metapackage, library + in-repo host | Metapackage covers controller/transpiler/client; library matches v0.7/v0.8 server package shape |
| 3 | Auth (operator side) | Projected SA token + `DelegatingHandler` injection | K8s-native; kubelet rotates atomically; zero runtime changes |
| 4 | Auth (runtime side) | Stock `AddJwtBearer` with K8s API as OIDC issuer | No K8s-specific code on runtime; standard OIDC discovery works |
| 5 | Reconcile diff | SHA-256 of canonical-JSON(spec) vs. `status.manifestRevision` | Idempotent across reconcile retries; trivial to compute; stable under key-ordering |
| 6 | Finalizer | `vais.io/agent-deactivate` → `EvictAsync` unless `preserveOnDelete=true` | Protects state; respects user intent |
| 7 | Conditions | `Ready`, `Synced`, `ManifestValid` (3) | Mirrors K8s built-in patterns; small enough to audit, rich enough to debug |
| 8 | Phase enum | `Pending / Creating / Active / Updating / Error / Terminating` (6) | Covers the 6 operator-local states; `Pod.status.phase` precedent |
| 9 | Secret resolution | `spec.secretRefs` + operator-side resolve before upsert | K8s-idiomatic muscle memory; runtime stays agnostic |
| 10 | Secret rotation | `V1Secret` watch → re-reconcile → `UpdateAsync` | Standard K8s controller pattern; honours K8s as source of truth |
| 11 | Tenancy binding | Explicit `vais.io/tenant-id` annotation | Decouples K8s namespace topology from tenant topology |
| 12 | RBAC scope | ClusterRole + ClusterRoleBinding, `WATCH_NAMESPACES` to narrow | Single operator can serve multi-tenant cluster; ops-configurable narrowing |

### Open questions (low-stakes, resolve during impl)

1. **KubeOps version pin**. Take the latest stable at PR 1 start (9.4.x if available; 9.0.x otherwise). Add to `Directory.Packages.props` once chosen.
2. **`AgentPhase` wire casing**. PascalCase (K8s idiom, matches `Pod.status.phase`). `[JsonStringEnumConverter(JsonNamingPolicy.Pascal)]` on the property.
3. **`SecretKeyReference` shape**. Mirrors K8s `SecretKeySelector` but without `optional` for v0.13 (missing → reconcile error).
4. **Dockerfile base image**. `mcr.microsoft.com/dotnet/aspnet:9.0-alpine` for size; non-root USER; expose only the health port.
5. **Helm chart version**. Chart `version: 0.1.0`, `appVersion: 0.13.0-preview`. Separate SemVer tracks — chart structure vs. operator image.
6. **Unknown-field preservation on `AgentSpec.OutputSchema` and `AgentSpec.Reasoning.*`**. Verify KubeOps transpiler emits `x-kubernetes-preserve-unknown-fields: true` on these during PR 1 CRD-YAML emission; if not, decorate the properties with an explicit attribute.
7. **Idempotency-Key scope**. `{uid}:{generation}:{verb}` per call. Verify v0.11 idempotency middleware's TTL (24h) is long enough to cover long reconcile queues; set operator's HTTP client with 30s timeout + 3 retries with exponential backoff.
8. **Token cache TTL**. 5 minutes with file-mtime re-read on each hit. Balance between avoiding repeated filesystem reads and timely rotation detection.
9. **`ServiceAccountPrincipalMapper` default**. Register optionally — operator package exposes DI extension; consumers opt in via `services.AddSingleton<IPrincipalMapper, ServiceAccountPrincipalMapper>()`. Default mapper stays as v0.6's `DefaultPrincipalMapper`.
10. **Helm chart CRD-install hook ordering**. Pre-install hook at weight `-10`; deployment at default weight `0`. Helm applies CRD, waits for establishment, then creates Deployment.

---

## Packages

**New packages (1):**
- **`Vais.Agents.Control.KubernetesOperator`** — library NuGet. Depends on `KubeOps.Operator 9.x`, `Vais.Agents.Abstractions`, `Vais.Agents.Control.Abstractions`, `Vais.Agents.Control.Http.Client`, `Microsoft.Extensions.Hosting 10.0.6`. Publishes `AgentEntity`/`AgentSpec`/`AgentStatus`/`AgentCondition`/`AgentPhase`/`AgentHandleRef`/`SecretKeyReference` public types + `AgentEntityController` + `ServiceAccountTokenHandler` + `ServiceAccountPrincipalMapper` + `KubernetesOperatorOptions` + `AddAgentKubernetesOperator` DI extension.

**New in-repo-only projects (2, not published as NuGet):**
- **`Vais.Agents.Control.KubernetesOperator.Host`** — exe project. Dockerfile source. `Program.cs` builds generic host, calls `AddAgentKubernetesOperator`, runs.
- **`Vais.Agents.Control.KubernetesOperator.Tests`** — test project. Unit tests with mocked `IAgentControlPlaneClient` + `IKubernetesClient`.

**New non-code artefacts:**
- **`oss/agentic/deploy/helm/vais-agents-operator/`** — Helm chart (Chart.yaml + values.yaml + templates for Deployment, ServiceAccount, ClusterRole, ClusterRoleBinding, CRD-install hook, projected-volume config).
- **`oss/agentic/deploy/crds/vais.io_agents.yaml`** — generated CRD (via `dotnet kubeops generate` during PR 1).
- **`oss/agentic/src/Vais.Agents.Control.KubernetesOperator.Host/Dockerfile`** — multi-stage build.

---

## Delivery

### PR 1 — Package skeleton + CRD types

**Packages**: new `Vais.Agents.Control.KubernetesOperator` (library). Plus new `Vais.Agents.Control.KubernetesOperator.Tests` test project.

Tasks:

- [x] New csproj `Vais.Agents.Control.KubernetesOperator.csproj` targeting `net9.0`, enabling PublicAPI analyzer. RootNamespace `Vais.Agents.Control.Kubernetes` (drops "Operator" suffix per `Vais.Agents.Control.Http.Server` → `Vais.Agents.Control.Http` precedent).
- [x] Added `KubeOps.Operator` + `KubeOps.Abstractions` at **10.3.4** (latest stable — spike said "9.x"; reality is 10.x family; net9.0 supported cleanly). CPM entries in `Directory.Packages.props`.
- [x] Types (all public with XML docs):
  - [x] `AgentEntity : CustomKubernetesEntity<AgentSpec, AgentStatus>` with `[KubernetesEntity(Group = "vais.io", ApiVersion = "v1alpha1", Kind = "Agent", PluralName = "agents")]` + `[KubernetesEntityShortNames("vagent", "vagents")]`. Plus 6 public `const string` fields — `EntityGroup` / `EntityApiVersion` / `EntityKind` / `EntityPluralName` / `DeactivateFinalizer` / `TenantIdAnnotation` — for consumer-code discoverability.
  - [x] `AgentSpec` class (not record — K8s deserialisation wants parameterless ctor + mutable properties; record auto-synthesised `Deconstruct`/`<Clone>$`/`operator==` bloat the PublicAPI baseline by ~100 entries with no wire benefit). 23 properties mirroring `AgentManifest` + new `SecretRefs: IDictionary<string, SecretKeyReference>?` + `PreserveOnDelete: bool`. Reuses `AgentHandlerRef`/`ProtocolBinding`/`ToolRef`/etc. from `Vais.Agents.Abstractions` (single-source-of-truth on the field shapes).
  - [x] `AgentStatus` class with 7 properties — `AgentHandle: AgentHandleRef? + ManifestRevision: string? + Phase: AgentPhase = Pending + LastReconciledAt: DateTimeOffset? + LastError: string? + Conditions: IList<AgentCondition>? + ObservedGeneration: long`.
  - [x] `AgentHandleRef(string AgentId, string Version, string? InstanceId = null)` record.
  - [x] `AgentCondition(string Type, string Status, string Reason, string Message, DateTimeOffset LastTransitionTime, long ObservedGeneration)` record.
  - [x] `AgentPhase` enum (6 values: Pending / Creating / Active / Updating / Error / Terminating). PascalCase-converter attribute deferred to PR 2 — STJ default PascalCase happens to work for enums.
  - [x] `SecretKeyReference(string Name, string Key)` record.
  - [x] `KubernetesOperatorAuthMode` enum (ServiceAccount / ClientCredentials).
- [x] XML docs on every public type + property.
- [x] `KubernetesOperatorOptions` class — populated (not stubbed) with 8 properties (ControlPlaneBaseUrl / ControlPlaneAudience / TokenPath / AuthMode / WatchNamespaces / TokenCacheTtl / ReconcileBackoffInitial / ReconcileBackoffMax) + defaults. Shape freeze in PR 1 keeps PR 2 focused on consuming, not defining.
- [x] `AgentKubernetesOperatorServiceCollectionExtensions.AddAgentKubernetesOperator(IServiceCollection, Action<KubernetesOperatorOptions>?)` stub. Registers options; throws `NotImplementedException` explaining "PR 2 lands reconcile controller".
- [ ] **CRD YAML emission BLOCKED**: `kubeops generate operator` fails transpiling `System.TimeSpan` in the reachable type graph (`AutoscalingSpec.IdleTtl` + `RunBudget.MaxDuration` in Abstractions). KubeOps 10.3.4 transpiler has no built-in TimeSpan mapping. PR 2 decides between (a) operator-local mirror types with TimeSpan → string (ISO 8601 duration), (b) hand-written CRD YAML with `x-kubernetes-preserve-unknown-fields: true` on the whole spec, or (c) upstream fix to KubeOps. Moved to PR 2's task list.
- [x] `PublicAPI.Shipped.txt` empty + `PublicAPI.Unshipped.txt` baseline: 156 entries (AgentCondition + AgentEntity + AgentHandleRef + AgentKubernetesOperatorServiceCollectionExtensions + AgentPhase + AgentSpec + AgentStatus + KubernetesOperatorAuthMode + KubernetesOperatorOptions + SecretKeyReference and all their members).
- [x] Test project `Vais.Agents.Control.KubernetesOperator.Tests.csproj` with 3 smoke tests in `AgentEntityJsonRoundTripTests.cs`: (1) `AgentSpec_WithRepresentativeFields_RoundTripsThroughJson` — builds a realistic AgentSpec (handler + protocols + tools + labels + secretRefs + preserveOnDelete), round-trips through `JsonSerializerDefaults.Web`, asserts field-by-field equality; (2) `AgentStatus_WithRepresentativeFields_RoundTripsThroughJson` — builds a status with handle + all 3 conditions + observedGeneration, round-trips + asserts; (3) `AgentEntity_Constants_MatchExpectedCrdMetadata` — guards the 6 const strings against accidental drift.
- [x] Solution file: both projects added via `dotnet sln add`.
- [x] No controller, no Helm chart, no Dockerfile (per plan).

### PR 2 — Controller + reconcile + SA-token handler + secret resolver

**Packages**: `Vais.Agents.Control.KubernetesOperator` (extend).

Tasks:

- [x] `AgentEntityController : IEntityController<AgentEntity>` — full reconcile logic per Q4's 6-row decision table:
  1. New CR (no finalizer) → add finalizer, set phase=Pending, status conditions=[Ready: Unknown], requeue immediately.
  2. CR with finalizer, no handle → resolve secrets, project `AgentSpec → AgentManifest`, compute specHash, call `CreateAsync(manifest, ct)` with Idempotency-Key, store handle + revision + phase=Active + conditions all True.
  3. CR with finalizer + handle, hash matches → refresh `lastReconciledAt`, no runtime call.
  4. CR with finalizer + handle, hash differs → call `UpdateAsync(handle, newManifest, ct)` with Idempotency-Key, update handle (new version) + revision + phase=Active.
  5. CR with deletionTimestamp set, `preserveOnDelete=false` → phase=Terminating, call `EvictAsync(handle, ct)`, on success remove finalizer.
  6. CR with deletionTimestamp set, `preserveOnDelete=true` → phase=Terminating, remove finalizer without runtime call.
- [x] Exponential-backoff requeue on error paths via `ReconciliationResult<AgentEntity>.Failure(entity, reason, ex, backoff)` — KubeOps 10.x pattern supersedes the earlier `EntityRequeue` injection approach. Initial backoff = `KubernetesOperatorOptions.ReconcileBackoffInitial` (5s default).
- [x] `KubernetesSecretResolver` (internal) — resolves each `spec.secretRefs[logicalName]` via `IKubernetesClient.GetAsync<V1Secret>(name, ns)`, extracts `data[key]`, base64-decodes to string. Batched by distinct secret name to minimise API calls. Throws `SecretResolutionException` on missing secret or missing key.
- [x] `ServiceAccountTokenHandler : DelegatingHandler`:
  - [x] Reads token from `KubernetesOperatorOptions.TokenPath` (default `/var/run/secrets/tokens/vais-runtime-token`).
  - [x] `TokenCacheTtl` + file-mtime check — TTL is primary freshness signal, mtime is secondary invalidation hint for mid-TTL rotation.
  - [x] Injects `Authorization: Bearer <token>` on `SendAsync`.
  - [x] Graceful fallback when `AuthMode == ClientCredentials` — pass-through without header injection (consumer wires their own auth).
- [x] `ServiceAccountPrincipalMapper : IPrincipalMapper` — maps K8s SA token claims:
  - [x] `system:serviceaccount:<ns>:<sa>` sub claim → `AgentPrincipal.Id` (whole sub) + `AgentPrincipal.TenantId` (namespace only).
  - [x] Falls back to `sub` / `tenant_id` claims for non-SA tokens — safe for mixed-auth runtime deployments.
  - [x] Not registered in the DI default — consumers opt in via `services.AddSingleton<IPrincipalMapper, ServiceAccountPrincipalMapper>()`.
- [x] `KubernetesOperatorOptions` — already populated in PR 1 (8 properties with defaults). PR 2 consumes them.
- [x] `AddAgentKubernetesOperator(IServiceCollection, Action<KubernetesOperatorOptions>?)` filled in:
  - [x] `services.Configure<KubernetesOperatorOptions>` when configurator provided, otherwise `AddOptions<>`.
  - [x] `TryAddSingleton(TimeProvider.System)` + transient registrations for `IKubernetesSecretResolver` + `IAgentEntityKubernetesClient` + `ServiceAccountTokenHandler`.
  - [x] `services.AddKubernetesOperator().AddController<AgentEntityController, AgentEntity>().AddFinalizer<AgentEntityFinalizer, AgentEntity>(AgentEntity.DeactivateFinalizer)` — KubeOps 10.x pipeline.
  - [x] `services.AddHttpClient<IAgentControlPlaneClient, AgentControlPlaneClient>(...).AddHttpMessageHandler<ServiceAccountTokenHandler>()` — fail-fast on missing `ControlPlaneBaseUrl`.
- [x] Internal helper `AgentSpecProjector.ToManifest(AgentSpec spec) : AgentManifest` — field-by-field projection. **v0.13 limitation**: `SecretRefs` NOT injected into the manifest envelope. Runtime still resolves secrets via `env:` / `file:` URIs set directly in manifest fields by the CR author; operator-side K8s secret injection is deferred (requires a runtime-side inline-secret wire format).
- [x] Internal helper `SpecHasher.Compute(AgentSpec spec) : string` — SHA-256 over canonical-JSON via `JsonNode` tree + recursive alphabetical key sort; null values elided. Returns `sha256:<hex>`.
- [x] Internal helper `IdempotencyKeyFactory.Build(uid, generation, verb) : string` — `{uid}:{generation}:{verb}` composition. Three verb constants (`create`, `update`, `evict`).
- [x] Internal helper `AgentConditions` — factory statics for `Ready`, `Synced`, `ManifestValid` conditions + `StatusTrue/False/Unknown` constants.
- [x] Internal abstraction `IAgentEntityKubernetesClient` (one method: `UpdateStatusAsync`) + default impl `AgentEntityKubernetesClient` wrapping `KubeOps.KubernetesClient.IKubernetesClient` — keeps the test surface narrow without forcing fakes to implement the full ~17-method `IKubernetesClient` interface.
- [x] `AgentEntityFinalizer : IEntityFinalizer<AgentEntity>` — deletion path. Sets `phase=Terminating` → calls `EvictAsync(agentId, version)` → returns `Success`. Skips eviction when `PreserveOnDelete=true` or when `Status.AgentHandle` is null (never succeeded a create). On eviction failure, returns `Failure(entity, ..., backoff=10s)`.
- [x] `PublicAPI.Unshipped.txt` updated — added `ServiceAccountTokenHandler` (2 entries) + `ServiceAccountPrincipalMapper` (3 entries). Everything else is internal so no PublicAPI churn.
- [x] `Microsoft.Extensions.Http 10.0.6` added to `Directory.Packages.props` (for `AddHttpClient` extension).
- [x] `InternalsVisibleTo="Vais.Agents.Control.KubernetesOperator.Tests"` on the csproj — test project reaches internal helpers directly.
- [x] Tests — **34 total in 6 files**:
  - [x] `AgentEntityJsonRoundTripTests.cs` (3 from PR 1, still green).
  - [x] `SpecHasherTests.cs` (5): same-spec → same hash, agent-id change → different hash, dictionary-key-order invariance, null-vs-absent equivalence, preserve-on-delete flip → different hash.
  - [x] `AgentSpecProjectorTests.cs` (4): required-fields copy, optional init-properties copy, `SecretRefs` NOT injected (documents v0.13 limitation), null optionals stay null.
  - [x] `IdempotencyKeyFactoryTests.cs` (5): composition, idempotence, generation discrimination, verb discrimination, empty-uid throws.
  - [x] `ServiceAccountTokenHandlerTests.cs` (5): SA mode injects bearer, client-credentials mode skips injection, reads once when mtime unchanged, refreshes on mtime change, TTL expiry forces re-read even with same mtime.
  - [x] `AgentEntityControllerTests.cs` (8): deletion-timestamp defers to finalizer, new CR create path with idempotency key, hash-match no-op, hash-differs update path, secret-refs resolved before create (success), missing secret sets `ManifestValid=False`, `CreateAsync` exception sets `Error` phase + backoff, `DeletedAsync` returns success immediately.
  - [x] `AgentEntityFinalizerTests.cs` (4): preserve-on-delete skips eviction, normal eviction path, eviction failure returns backoff, no-handle skips eviction.

### PR 3 — Operator host + Helm chart + Dockerfile

**Packages**: no new NuGet packages. New in-repo project `Vais.Agents.Control.KubernetesOperator.Host` + Helm chart + Dockerfile + integration-ish test.

Tasks:

- [x] New exe project `Vais.Agents.Control.KubernetesOperator.Host.csproj` targeting `net9.0`, `OutputType=Exe`, `IsPackable=false`. Uses `Microsoft.NET.Sdk.Web` SDK (for `WebApplication.CreateBuilder` health endpoints).
- [x] `Program.cs`:
  - [x] `WebApplication.CreateBuilder(args)` + `ConfigureKestrel` listening on port 8080.
  - [x] Config bound from `Vais:KubernetesOperator` section via `builder.Configuration.GetSection(...).Bind(opts)`. Env-var override via standard `Vais__KubernetesOperator__*` convention.
  - [x] `services.AddAgentKubernetesOperator(opts => ...)`.
  - [x] `app.MapGet("/healthz", ...)` + `/readyz` minimal endpoints.
  - [x] `public partial class Program;` marker for future `WebApplicationFactory` integration.
- [x] `appsettings.json` — default values populated; `ControlPlaneBaseUrl` null (required to set at install).
- [x] `Dockerfile` (multi-stage) at `src/Vais.Agents.Control.KubernetesOperator.Host/Dockerfile`:
  - [x] Stage 1 `mcr.microsoft.com/dotnet/sdk:9.0-alpine` → restore → publish with layer-optimised csproj-copy ordering.
  - [x] Stage 2 `mcr.microsoft.com/dotnet/aspnet:9.0-alpine` → non-root `USER vais:vais` (uid 65532) + `EXPOSE 8080` + `HEALTHCHECK wget /healthz` + `ENTRYPOINT ["dotnet", "...Host.dll"]`.
  - [x] `.dockerignore` excluding `bin/`, `obj/`, `*.user`, `.vs`, `.idea`, `.git`, `artifacts/`.
- [x] **CRD YAML** (deferred from PR 2) — hand-written at `deploy/crds/vais.io_agents.yaml` since KubeOps 10.3.4 transpiler blocks on TimeSpan in the reachable type graph. Uses `x-kubernetes-preserve-unknown-fields: true` on `.spec` + `.status` (full AgentManifest field set round-trips without per-field OpenAPI validation). Explicitly declares `status` subresource + `additionalPrinterColumns` for AgentId / Version / Phase / Ready / Age columns shown by `kubectl get vagent`.
- [x] Helm chart at `oss/agentic/deploy/helm/vais-agents-operator/`:
  - [x] `Chart.yaml` (version 0.1.0, appVersion 0.13.0-preview, kubeVersion `>=1.28.0-0`).
  - [x] `values.yaml` — image repository/tag/pullPolicy + replicaCount + controlPlane.{baseUrl, audience} + auth.{mode, tokenPath, tokenExpirationSeconds} + watchNamespaces + rbac.{create, installCrd} + resources + podSecurityContext + containerSecurityContext + imagePullSecrets.
  - [x] `templates/_helpers.tpl` — standard labels + selectorLabels + fullname + serviceAccountName helpers.
  - [x] `templates/deployment.yaml` — single-replica Deployment, projected-volume mount for SA token, env vars mapped to `KubernetesOperatorOptions`, liveness + readiness probes on port 8080, security contexts (runAsNonRoot, readOnlyRootFilesystem, drop all caps).
  - [x] `templates/serviceaccount.yaml` — SA named `{{ .Release.Name }}-operator`.
  - [x] `templates/clusterrole.yaml` — 6 rule sets: `agents` (full), `agents/status`, `agents/finalizers`, `secrets` (get/list/watch), `events` (create/patch).
  - [x] `templates/clusterrolebinding.yaml` — binds ClusterRole to SA.
  - [x] `templates/crd.yaml` — CRD with `helm.sh/hook: pre-install,pre-upgrade` + `hook-weight: -10` + `hook-delete-policy: before-hook-creation` (mirrors `deploy/crds/vais.io_agents.yaml`).
  - [x] `README.md` — quick-start + full values reference + uninstall + known limitations.
- [x] Tests — **8 new, across 3 files**:
  - [x] `FullReconcileFlowTests.cs` (1) — end-to-end `Create → no-op reconcile → Update → deletion-timestamp defers → finalizer.FinalizeAsync calls EvictAsync`. Exercises the controller + finalizer composed against fake control-plane + K8s clients; asserts 3 idempotency keys (`uid:1:create` / `uid:2:update`) + phase progressions + handle version bump.
  - [x] `ServiceCollectionCompositionTests.cs` (2) — registrations present for `IKubernetesSecretResolver`, `IAgentEntityKubernetesClient`, `ServiceAccountTokenHandler`, `TimeProvider`, `IConfigureOptions<KubernetesOperatorOptions>`; options bind correctly through `IOptions<>` (ControlPlaneBaseUrl / AuthMode / TokenCacheTtl round-trip via `BuildServiceProvider`).
  - [x] `HelmChartShapeTests.cs` (5) — drift guards on shipped YAML: Chart.yaml carries appVersion `0.13.0-preview`; Deployment template declares env-var keys matching `KubernetesOperatorOptions` binding + health endpoint paths + serviceAccountToken volume; ClusterRole grants the 6 required verbs + resources; standalone CRD YAML registers Agent kind + preserve-unknown-fields; values.yaml carries `controlPlane.baseUrl` placeholder.
- [x] `oss/agentic/deploy/README.md` — deployment quick-start (docker build / helm install / apply sample CR / inspect / teardown).
- [x] **Full non-container suite**: **611/611** (569 v0.12 baseline + 42 PR 1+2+3 new, zero regressions).

### PR 4 — v0.13.0-preview cut

**Packages**: all 23 (22 existing + 1 new) for the cut.

Tasks:

- [x] **API freeze**: promoted `Unshipped` → `Shipped` on `Vais.Agents.Control.KubernetesOperator` — 161 entries. Other 22 packages ship unchanged since `v0.12.0-preview`. Commit `3a66e99`.
- [x] **Pack**: `dotnet pack Vais.Agents.sln -c Release -p:VersionPrefix=0.13.0 -p:VersionSuffix=preview -o artifacts/packages` → 23 `.nupkg` + 23 `.snupkg`. The new `Vais.Agents.Control.KubernetesOperator.0.13.0-preview.nupkg` joined the feed; `Vais.Agents.Control.KubernetesOperator.Host` (IsPackable=false) + test project excluded.
- [x] **Smoketest**: bumped all 23 package refs to `0.13.0-preview`; added `Vais.Agents.Control.KubernetesOperator` ref + K8s-operator probe exercising entity construction + `[KubernetesEntity]`/`[KubernetesEntityShortNames]` reflection + `AgentSpec` JSON round-trip + 9-type type-probe (AgentEntity/AgentSpec/AgentStatus/AgentPhase/SecretKeyReference/KubernetesOperatorOptions/ServiceAccountTokenHandler/ServiceAccountPrincipalMapper/AgentKubernetesOperatorServiceCollectionExtensions). Probe line: `Kubernetes operator: entity-kind=Agent group=vais.io apiversion=v1alpha1 short-names=[vagent,vagents] phase-enum-values=6 status-conditions=3 secret-refs-supported=True preserve-on-delete-default=false finalizer=vais.io/agent-deactivate operator-types-probed=9`. Final line updated to `"All twenty-three Vais.Agents.* 0.13.0-preview packages consumed cleanly from a plain .NET 9 console app."` Ran clean.
- [ ] **Container image** (optional, deferred as documented): `docker build -t vais-agents-operator:0.13.0-preview -f src/Vais.Agents.Control.KubernetesOperator.Host/Dockerfile .` — deferred; repo ships Dockerfile, users `docker build + push` to their own registry. No public pipeline in v0.13.
- [x] **Tag**: annotated `v0.13.0-preview` created on OSS `main` at commit `3a66e99` (API freeze). Not pushed.
- [x] **Milestone log** entry appended to [`actor-agents-oss-milestone-log.md`](./actor-agents-oss-milestone-log.md).
- [x] **Research doc §7** update — "Kubernetes CRDs + operator" backlog line struck through, pointed at this pillar + findings doc.
- [x] **CRD YAML committed**: `deploy/crds/vais.io_agents.yaml` (standalone kubectl-apply target) + `deploy/helm/vais-agents-operator/templates/crd.yaml` (chart-embedded with pre-install hook). Both in the feat bundle `623a47c`.
- [ ] **Acceptance demo (manual)**: user's docker-desktop K8s — `kubectl apply -f deploy/crds/vais.io_agents.yaml` + `docker build` + `helm install` + apply sample CR + observe reconcile + delete CR. Deferred to a user-driven session; unit + integration-ish tests cover the automated equivalents.

---

## Exit criteria

- [ ] All 4 PRs on OSS repo `main`, landed as two-commit pattern (feat PRs 1–3; chore PR 4 API freeze) matching v0.7 → v0.12 cadence.
- [ ] One new NuGet library package; in-repo host + tests + Helm chart not packaged but in-tree + compile clean.
- [ ] Full non-container test suite green: **569 + ~12 new = ~581 tests**. Exact number TBD as unit-test scope firms up during PR 2.
- [ ] Smoketest probes the K8s-operator library surface — constructs `AgentEntity`, reads `[KubernetesEntity]` metadata, JSON round-trips, from a fresh .NET 9 console project with only NuGet references.
- [ ] `v0.13.0-preview` tag created on the API-freeze commit.
- [ ] **Acceptance demo (manual)**: on user's `docker-desktop` K8s:
  1. `kubectl apply -f deploy/crds/vais.io_agents.yaml` — CRD registers.
  2. `docker build -t vais-agents-operator:0.13.0-preview -f src/Vais.Agents.Control.KubernetesOperator.Host/Dockerfile .`.
  3. `helm install vais-agents-operator deploy/helm/vais-agents-operator/ --set image.repository=vais-agents-operator --set controlPlane.baseUrl=http://host.docker.internal:5080` — operator deploys.
  4. `kubectl apply -f samples/agent-sample.yaml` — Agent CR created.
  5. Verify: operator logs show reconcile + CreateAsync call; fake/local control-plane HTTP host receives the call; `kubectl get vagent chat-assistant -o yaml` shows populated `.status` with non-null `agentHandle` + `phase: Active` + conditions all True.
  6. `kubectl delete vagent chat-assistant` — finalizer fires, EvictAsync called, CR garbage-collected within one reconcile cycle.
  - Deferred automation: kind-in-CI. This round, manual acceptance against docker-desktop is enough.

---

## Decisions locked (from the spike + research walkthrough 2026-04-20)

- **Single CRD**: `Agent`. `AgentGraph` → v0.14; `AgentRun` → v0.15.
- **CRD**: `[KubernetesEntity(vais.io/v1alpha1, Agent)]` + `CustomKubernetesEntity<AgentSpec, AgentStatus>`. Namespaced. Short names `vagent`, `vagents`.
- **`AgentSpec`** mirrors `AgentManifest` + `SecretRefs` + `PreserveOnDelete`.
- **`AgentStatus`** = handle + revision + phase + lastReconciledAt + lastError + conditions[] + observedGeneration. Phase = 6 states; conditions = `Ready` / `Synced` / `ManifestValid`.
- **KubeOps.Operator 9.x metapackage** on .NET 9.
- **Library + in-repo-only host + tests**. Package count 22 → 23.
- **Operator auth**: projected SA token + `DelegatingHandler`. 5-min cache.
- **Runtime auth**: stock `AddJwtBearer` + K8s API OIDC discovery. Out-of-cluster fallback = static client-credentials.
- **Reconcile diff**: SHA-256 of canonical-JSON(spec) vs. `status.manifestRevision`.
- **Finalizer** `vais.io/agent-deactivate` → `EvictAsync` unless `preserveOnDelete=true`.
- **Idempotency-Key** `{uid}:{generation}:{verb}` on every operator → runtime call.
- **Secrets**: `spec.secretRefs` + operator-side resolve + `V1Secret` watch for rotation. Runtime unchanged. Audit redaction dismissed.
- **Tenancy**: `vais.io/tenant-id` annotation + optional `ServiceAccountPrincipalMapper`.
- **RBAC**: ClusterRole + ClusterRoleBinding by default; `WATCH_NAMESPACES` narrows.
- **Helm chart at `deploy/helm/vais-agents-operator/`**. CRD install as pre-install hook.
- **Container image**: built locally; public image pipeline deferred.

---

## Progress log

- 2026-04-20 — plan created after the Kubernetes-operator spike closed. 12 decisions locked from the spike's verdict; 4 PRs scoped; 10 open questions flagged for impl. Package count 22 → 23 (one new library). Two in-repo-only projects (host exe + tests) + Helm chart + Dockerfile + generated CRD YAML. Target effort: ~3 days focused work (PR 1 is types + CRD-YAML emission; PR 2 is the bulk — controller + reconcile + SA-token handler + secret resolver + ~12 tests; PR 3 is operator host + Helm chart + Dockerfile + 3 integration-ish tests; PR 4 is the cut/pack rote). **Pending**: start on PR 1 (package skeleton + CRD types + CRD YAML generation).
- 2026-04-20 — PR 1 landed on `033-logging-improvement-read`. New library package `Vais.Agents.Control.KubernetesOperator` (RootNamespace `Vais.Agents.Control.Kubernetes`) + new test project `Vais.Agents.Control.KubernetesOperator.Tests`. 10 public types: `AgentEntity` (CR, inherits `KubeOps.Abstractions.Entities.CustomKubernetesEntity<AgentSpec, AgentStatus>`) + `AgentSpec` (23 properties mirroring `AgentManifest` + `SecretRefs` + `PreserveOnDelete`) + `AgentStatus` (7 properties) + `AgentHandleRef` / `AgentCondition` / `SecretKeyReference` (records) + `AgentPhase` / `KubernetesOperatorAuthMode` (enums) + `KubernetesOperatorOptions` (populated, 8 properties) + `AgentKubernetesOperatorServiceCollectionExtensions` (stub throwing `NotImplementedException` pending PR 2). KubeOps pinned at **10.3.4** (spike said 9.x, but latest stable is 10.x; net9.0 supported). `KubeOps.Operator` + `KubeOps.Abstractions` added to `Directory.Packages.props`. `PublicAPI.Unshipped.txt` baseline = 156 entries. 3 smoke tests (AgentSpec / AgentStatus JSON round-trip + const-metadata drift-guard) in `AgentEntityJsonRoundTripTests.cs` — all green. Full non-container suite: **572/572** (569 baseline + 3 new). **Shape adjustments during impl**: (1) Used classes with `{ get; set; }` for `AgentSpec` / `AgentStatus` (K8s deserialisation expects parameterless ctor + mutable properties; records would add ~100 auto-synthesised PublicAPI entries — `<Clone>$`, `Deconstruct`, `operator==`, per-property-equals — for zero wire benefit). (2) `AgentSpec` reuses `AgentHandlerRef` / `ProtocolBinding` / `ToolRef` / `ModelSpec` / etc. from `Vais.Agents.Abstractions` — single source of truth for manifest field shapes. (3) `[JsonStringEnumConverter(JsonNamingPolicy.Pascal)]` attribute deferred to PR 2 — STJ default enum-serialisation already PascalCases. (4) `AgentEntity` ships 6 `public const string` fields (EntityGroup/EntityApiVersion/EntityKind/EntityPluralName/DeactivateFinalizer/TenantIdAnnotation) for consumer-code discoverability. **CRD YAML emission BLOCKED**: `kubeops generate operator` transpiler throws `ArgumentException: The given type System.TimeSpan is not a valid Kubernetes entity` when walking the type graph from `AgentSpec` — `AutoscalingSpec.IdleTtl` + `RunBudget.MaxDuration` (both in Abstractions) carry `TimeSpan?`. KubeOps 10.3.4 transpiler has no built-in TimeSpan mapping. Moved to PR 2's task list — three resolutions on the table: (a) operator-local mirror CR types with TimeSpan → ISO-8601 string, (b) hand-written CRD YAML with `x-kubernetes-preserve-unknown-fields: true` on the whole spec, (c) upstream fix to KubeOps. Lean pick will likely be (a) for a small subset or (b) for the pragmatic punt. **Pending**: PR 2 (controller + reconcile + SA-token handler + secret resolver + CRD YAML decision + ~12 tests).
- 2026-04-20 — PR 2 landed on `033-logging-improvement-read`. Controller + reconcile + finalizer + SA-token handler + K8s secret resolver + DI wiring + 31 new unit tests. Production additions to `Vais.Agents.Control.KubernetesOperator`: `SpecHasher` (internal, SHA-256 canonical-JSON via `JsonNode` tree + alphabetical key sort), `AgentSpecProjector` (internal, `AgentSpec → AgentManifest` field-by-field copy; **v0.13 limitation**: does NOT inject `SecretRefs` into manifest envelope — runtime uses `env:`/`file:` URIs set by the CR author; operator-side secret injection deferred pending runtime-side inline-secret wire format), `IdempotencyKeyFactory` (internal, `{uid}:{generation}:{verb}` with 3 verb constants), `AgentConditions` (internal static factories for the 3 condition types), `IKubernetesSecretResolver` + `KubernetesSecretResolver` (internal; batches by distinct secret name; `SecretResolutionException` on missing secret or missing key), `ServiceAccountTokenHandler` (public sealed, bearer-token injection with TTL+mtime cache, `ClientCredentials` mode passes through without header), `ServiceAccountPrincipalMapper` (public sealed, `system:serviceaccount:<ns>:<sa>` → `AgentPrincipal.Id`+`TenantId`, falls back to stock `sub`/`tenant_id` claims for mixed-auth), `AgentEntityController` (internal sealed, implements `IEntityController<AgentEntity>` with 6-row reconcile decision table + per-phase status writes + `Idempotency-Key` on every control-plane call), `AgentEntityFinalizer` (internal sealed, implements `IEntityFinalizer<AgentEntity>`, calls `EvictAsync` unless `PreserveOnDelete=true` or no handle on status), `IAgentEntityKubernetesClient` + `AgentEntityKubernetesClient` (internal narrow wrapper around `IKubernetesClient.UpdateStatusAsync` — keeps test-fake surface minimal without implementing the full 17-method `IKubernetesClient` interface). `AddAgentKubernetesOperator` DI extension filled in — registers options + typed HttpClient with `ServiceAccountTokenHandler` + KubeOps operator builder with `.AddController<AgentEntityController, AgentEntity>()` + `.AddFinalizer<AgentEntityFinalizer, AgentEntity>(DeactivateFinalizer)`. `Microsoft.Extensions.Http 10.0.6` added to CPM. `InternalsVisibleTo="Vais.Agents.Control.KubernetesOperator.Tests"` on library csproj. 31 new unit tests across 5 files (SpecHasher: 5 / AgentSpecProjector: 4 / IdempotencyKeyFactory: 5 / ServiceAccountTokenHandler: 5 / AgentEntityController: 8 / AgentEntityFinalizer: 4) + the 3 smoke tests from PR 1 = **34 total**. Full non-container suite: **603/603** (569 baseline + 34 new, zero regressions). **Shape adjustments during impl**: (1) KubeOps 10.x `IEntityController<T>.ReconcileAsync` / `DeletedAsync` return `Task<ReconciliationResult<TEntity>>`, not `Task` — refactored controller + finalizer accordingly. `ReconciliationResult<T>.Success(entity)` / `.Failure(entity, reason, ex, backoff)` encodes requeue, superseding the earlier `EntityRequeue<T>` delegate-injection approach. (2) Secret-value injection into the manifest envelope was descoped from PR 2 — the shipped runtime's `ISecretResolver` composite expects `secret://scheme/path` URIs and rejects literal values, so operator-side resolved values can't flow into `ModelSpec.ApiKeyRef` or `OutboundCredentialRef.Ref` without a runtime-side wire-format change. v0.13 ships the resolver wiring (validates secrets exist, fails early with `SecretResolutionException` → `ManifestValid=False` condition) but keeps projection silent on secrets. Documented as the operator-side half of a future inline-secret pillar. (3) Introduced `IAgentEntityKubernetesClient` narrow internal abstraction over `IKubernetesClient.UpdateStatusAsync` — the full `IKubernetesClient` has ~17 methods; stubbing it in test-fakes would require boilerplate throw-NotImplementedException per method. (4) `SpecHasher` uses `JsonNode.Parse` → `JsonObject` key-sort rather than raw-string sort to stay robust to nested objects + arrays — tested against dict-key-order permutations. (5) `ServiceAccountTokenHandler` TTL semantics: TTL expiry unconditionally forces re-read even when mtime matches (TTL is primary freshness signal; mtime is secondary invalidation hint). (6) **CRD YAML emission still deferred** — moved to PR 3 bundle (alongside Helm chart + Dockerfile). KubeOps transpiler still fails on TimeSpan in `AutoscalingSpec.IdleTtl` + `RunBudget.MaxDuration`; PR 3 resolution likely = hand-written CRD with `x-kubernetes-preserve-unknown-fields` on the whole spec. **Pending**: PR 3 (operator Host exe + Dockerfile + Helm chart + CRD YAML + 3 integration-ish tests).
- 2026-04-20 — PR 3 landed on `033-logging-improvement-read`. Operator host exe + Dockerfile + Helm chart + hand-rolled CRD YAML + 8 integration-ish/shape tests. New in-repo-only project `Vais.Agents.Control.KubernetesOperator.Host` (Microsoft.NET.Sdk.Web, net9.0, IsPackable=false) — minimal `Program.cs` builds WebApplication, binds `Vais:KubernetesOperator` config section, calls `AddAgentKubernetesOperator`, maps `/healthz`+`/readyz`, Kestrel on :8080. `Dockerfile` multi-stage (sdk-alpine → aspnet-alpine, non-root `vais:vais` uid 65532, HEALTHCHECK wget) + `.dockerignore`. Hand-rolled CRD YAML at `deploy/crds/vais.io_agents.yaml` (PR 2 carryover) — KubeOps 10.3.4 transpiler still blocked on TimeSpan in type graph, so: top-level `x-kubernetes-preserve-unknown-fields: true` on `.spec` + `.status` + explicit enum constraint on `phase` (6 values) + explicit shape on `conditions[]` + `additionalPrinterColumns` for AgentId/Version/Phase/Ready/Age. Helm chart at `deploy/helm/vais-agents-operator/` — Chart.yaml (appVersion 0.13.0-preview, kubeVersion >=1.28.0-0), values.yaml (image + replicaCount + controlPlane.{baseUrl,audience} + auth.{mode,tokenPath,tokenExpirationSeconds} + watchNamespaces + rbac.{create,installCrd} + resources + podSecurityContext + containerSecurityContext), 6 templates (_helpers.tpl, serviceaccount.yaml, clusterrole.yaml with 6 rule sets, clusterrolebinding.yaml, deployment.yaml with projected-volume + env-var mapping + liveness/readiness probes, crd.yaml with `helm.sh/hook: pre-install,pre-upgrade` + hook-weight=-10), chart README with quick-start + values reference + uninstall + known-limitations. `oss/agentic/deploy/README.md` top-level quick-start (docker build / helm install / apply CR / teardown). 8 new tests across 3 files — `FullReconcileFlowTests.cs` (1, exercises create→no-op→update→finalizer-evict sequence end-to-end, asserts 3 idempotency keys + phase transitions), `ServiceCollectionCompositionTests.cs` (2, verifies DI registrations for IKubernetesSecretResolver/IAgentEntityKubernetesClient/ServiceAccountTokenHandler/TimeProvider/IConfigureOptions + options round-trip through BuildServiceProvider), `HelmChartShapeTests.cs` (5, drift guards via substring checks on env-var names + health endpoints + projected-volume + ClusterRole verbs + CRD shape — no YAML-parse dep). Full non-container suite: **611/611** (569 v0.12 baseline + 42 PR 1+2+3 new — 3 smoke tests from PR 1, 31 from PR 2, 8 from PR 3 — zero regressions). **Shape adjustments during impl**: (1) Host exe uses `Microsoft.NET.Sdk.Web` (not `Microsoft.NET.Sdk`) so `WebApplication.CreateBuilder` + minimal endpoints compose cleanly for the health probes. (2) Had to remove explicit `Content Include="appsettings.json"` — .NET SDK already includes `Content` items from project dir by default, duplicate items fire NETSDK1022. (3) CRD YAML has two copies (chart-embedded at `templates/crd.yaml` for `helm install` + standalone at `deploy/crds/vais.io_agents.yaml` for `kubectl apply`) — README flags them as needing manual sync pending PR 4 consolidation. (4) `HelmChartShapeTests` uses substring checks instead of YAML parsing (would need a test-only YamlDotNet dep) — lightweight drift guard, not full schema validation. (5) Acceptance demo against docker-desktop Kubernetes is a manual step deferred to PR 4. **Pending**: PR 4 (v0.13.0-preview cut — API freeze, pack 23, smoketest probe, tag, milestone log, research-doc strike-through).
- 2026-04-20 — PR 4 landed on OSS `main`. Two commits: `623a47c feat(k8s): Kubernetes operator pillar (v0.13 PRs 1-3)` (53 files, +3879; library + host exe + tests + Helm chart + CRD YAML + Dockerfile + deploy README) + `3a66e99 chore: API freeze for v0.13.0-preview — promote Unshipped -> Shipped` (2 files, 161 PublicAPI entries moved Unshipped → Shipped on the new library package; 22 existing packages unchanged). Annotated `v0.13.0-preview` tag created on `3a66e99` (not pushed). 23 `.nupkg` + 23 `.snupkg` packed at `0.13.0-preview` into `artifacts/packages/` — the new `Vais.Agents.Control.KubernetesOperator.0.13.0-preview.nupkg` joined the feed; `Vais.Agents.Control.KubernetesOperator.Host` (IsPackable=false) + test project excluded. Smoketest refreshed to `0.13.0-preview`; new Kubernetes operator library-surface probe exercises `AgentEntity` construction + `[KubernetesEntity]`+`[KubernetesEntityShortNames]` reflection + `AgentSpec` JSON round-trip + 9-type type-probe. Probe line: `Kubernetes operator: entity-kind=Agent group=vais.io apiversion=v1alpha1 short-names=[vagent,vagents] phase-enum-values=6 status-conditions=3 secret-refs-supported=True preserve-on-delete-default=false finalizer=vais.io/agent-deactivate operator-types-probed=9`. Final line: `"All twenty-three Vais.Agents.* 0.13.0-preview packages consumed cleanly from a plain .NET 9 console app."` Ran clean. Milestone log entry appended (`actor-agents-oss-milestone-log.md`). Research doc §7 "Kubernetes CRDs + operator" backlog line struck through and pointed at this pillar + findings doc. **Pillar closed.** Only follow-up remaining: the manual acceptance demo against user's docker-desktop K8s — unit + integration-ish tests are green (34 unit tests + 8 integration-ish tests across 10 files; full non-container suite 611/611).
