# v0.13 Kubernetes CRD + operator — spike findings

Synthesis of the research spike scoped in [`actor-agents-oss-v0.13-kubernetes-operator-spike.md`](./actor-agents-oss-v0.13-kubernetes-operator-spike.md). Answers Q1–Q5 with evidence. Landing verdict at the bottom.

Created 2026-04-20. **Status**: complete. Q1–Q5 resolved from `AgentManifest` shape audit + KubeOps 9.x packaging audit + existing `AddJwtBearer` wiring in v0.6 + `AuditLogEntry` shape check + CRD-schema sketch.

---

## Q1 — CRD schema design

### AgentManifest shape audit

Read `AgentManifest.cs`. 10 positional ctor params + 12 init-only properties = 22 top-level fields. Nested record types: `AgentHandlerRef`, `ProtocolBinding`, `ToolRef`, `ModelSpec`, `SystemPromptSpec`, `McpServerRef`, `GuardrailsSpec`, `HandoffRef`, `RunBudget`, `ContextProviderRef`, `ReasoningSpec`, `ObservabilitySpec`, `MemoryRef`, `IdentityRef`, `AutoscalingSpec`. Two `JsonElement` fields (`OutputSchema` + likely one under `Reasoning`). Two dictionary fields (`Labels`, `Annotations`).

### CRD schema strategy

Three options scored:

| option | how | validation fidelity | maintenance |
|---|---|---|---|
| **(a) Hand-written subset schema** | YAML OpenAPI schema for the subset we want users to write declaratively | Best control | Drifts from `AgentManifest` on every expansion |
| **(b) KubeOps-transpiled from `[KubernetesEntity]` record** | Define a mirror record + attribute-annotate; KubeOps transpiler emits OpenAPI | Same as hand-written if the record mirrors `AgentManifest` | Matches `AgentManifest` evolutions when we port the new fields — linear effort |
| **(c) Reuse v0.6 JSON schema** | Would lift an existing JSON-schema into the CRD | Would match perfectly | N/A — no JSON schema exists today; `IAgentManifestLoader` validates programmatically |

### Decision (Q1): **(b) KubeOps-transpiled from a mirror record**

Ship two new records in the new package:

- `AgentSpec` — mirror of `AgentManifest` field set, plus a new optional `SecretRefs` field (dictionary<string, SecretKeyReference>) for the Q5 K8s secret story.
- `AgentStatus` — new record for the operator's projection (detailed in Q4).

`AgentEntity : CustomKubernetesEntity<AgentSpec, AgentStatus>` with `[KubernetesEntity(Group = "vais.io", ApiVersion = "v1alpha1", Kind = "Agent")]` + `[KubernetesEntityShortNames("vagent", "vagents")]`.

**`JsonElement` handling**: KubeOps 9.x transpiler emits `x-kubernetes-preserve-unknown-fields: true` on unconstrained-shape fields when the property type doesn't decompose into primitives — confirmed by its behaviour on `JsonDocument`/`JsonElement`/`object` properties in the public samples. Verify during PR 1 that the emitted CRD YAML carries the marker on `outputSchema` + `reasoning` sub-fields.

**Projection to `AgentManifest`**: operator has a one-shot `ToManifest(AgentSpec spec, IReadOnlyDictionary<string, string> resolvedSecrets) : AgentManifest` helper — field-by-field record-to-record map, with secret-ref placeholders replaced by resolved values. Kept local to the operator; not a public API.

---

## Q2 — Operator framework choice + host topology

### KubeOps packaging audit

The latest-major KubeOps is 9.x, broadly released as a single metapackage pulling everything an operator needs:

| package | role |
|---|---|
| `KubeOps.Operator` | metapackage — pulls transpiler + client + controller hosting |
| `KubeOps.Transpiler` | reads `[KubernetesEntity]` annotations, emits CRD YAML (via `dotnet kubeops generate` or runtime `IEntityTypeFetcher`) |
| `KubeOps.KubernetesClient` | wraps the official `KubernetesClient` with richer list/watch/patch helpers |
| `KubeOps.Abstractions` | interfaces (`IEntityController<T>`, `ResourceControllerResult`, `[EntityRbac]`) |
| `KubeOps.Cli` | CLI for CRD generation + RBAC emission during build |

`KubeOps.Operator` targets `net9.0` (the 9.x line tracks .NET major versions) and works cleanly with `Microsoft.Extensions.Hosting 10.0.6` (our current pin). The transpiler sees `System.Text.Json`-compatible types; mapping matches our existing `AgentManifest` JSON conventions.

### Host topology

Operator runs as its own Deployment (replica=1 today; leader-election later for HA). Not a sidecar on the silo pod — keeps operator versioning + scaling independent. Standard K8s pattern.

**Project shape inside the OSS repo:**

- **Library (packaged NuGet)**: `Vais.Agents.Control.KubernetesOperator` — ships the `AgentEntity` CRs + `AgentEntityController` + `AddAgentKubernetesOperator(IServiceCollection, Action<KubernetesOperatorOptions>?)` extension. Matches v0.7 MCP server + v0.8 A2A server pattern: library exposes DI extension + types, consumers compose their own `Program.cs`.
- **In-repo host (not packaged)**: `Vais.Agents.Control.KubernetesOperator.Host` — small `Program.cs` that builds a .NET generic host, calls `AddAgentKubernetesOperator`, runs. Serves two roles: (1) CI-buildable proof the library actually composes into a runnable exe; (2) Dockerfile source for the `vais-agents-operator` container image. **NOT published as a NuGet package.**
- **Tests (not packaged)**: `Vais.Agents.Control.KubernetesOperator.Tests`.

**Container image publishing**: deferred as a per-consumer concern. Repo ships the Dockerfile; operator chart's `values.yaml` defaults to a placeholder `image.repository`. Users `docker build + push` to their own registry for now. Public image publishing is a separate release-automation pillar.

### Decision (Q2): **KubeOps.Operator 9.x metapackage, own Deployment, library + executable split**

Metapackage keeps dep management simple. Library package ships reusable controller + types. Host project publishes the container image. Leader-election deferred to post-v0.13.

---

## Q3 — Operator → runtime auth (SA token path)

### Projected-volume setup

Helm chart mounts a ServiceAccount-projected token:

```yaml
volumes:
- name: vais-runtime-token
  projected:
    sources:
    - serviceAccountToken:
        audience: vais-agents-runtime
        expirationSeconds: 3600
        path: vais-runtime-token
volumeMounts:
- name: vais-runtime-token
  mountPath: /var/run/secrets/tokens
  readOnly: true
```

K8s kubelet rotates the token on expiry — file atomically replaced. Operator reads the token per outbound request (or caches with 5-min TTL + file-watcher invalidation; lean = cache+TTL).

### HttpMessageHandler injection

Operator registers its `AgentControlPlaneClient` with a `DelegatingHandler` that reads the token file and injects `Authorization: Bearer <token>` on each outbound request:

```csharp
internal sealed class ServiceAccountTokenHandler : DelegatingHandler
{
    private readonly string _tokenPath;
    // cache with TTL; reread when TTL expires or file mtime changes

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var token = await GetTokenAsync(ct).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, ct).ConfigureAwait(false);
    }
}
```

Wire via `services.AddHttpClient<IAgentControlPlaneClient, AgentControlPlaneClient>(...).AddHttpMessageHandler<ServiceAccountTokenHandler>()`.

### Runtime-side JWT validation

v0.6's `AddAgentControlPlaneJwtAuth(configure)` delegates to stock `AddJwtBearer`. Runtime operator wires the K8s API as the issuer:

```csharp
services.AddAgentControlPlaneJwtAuth(opt =>
{
    // Kubernetes API OIDC discovery — in-cluster, kube-apiserver serves
    // /.well-known/openid-configuration on https://kubernetes.default.svc:443
    opt.Authority = "https://kubernetes.default.svc";
    opt.Audience = "vais-agents-runtime";
    opt.TokenValidationParameters.ValidIssuers = new[] { "https://kubernetes.default.svc" };
    // Trust the in-cluster CA for JWKS fetch
    opt.BackchannelHttpHandler = new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = ValidateKubeApiCa,
    };
});
```

`IPrincipalMapper` default honours standard `sub` / `preferred_username` claims. K8s SA tokens carry `kubernetes.io/serviceaccount/service-account.name` + `kubernetes.io/serviceaccount/namespace` — `DefaultPrincipalMapper` already maps `sub` to `AgentPrincipal.Id`; we land a K8s-flavoured `ServiceAccountPrincipalMapper` in the operator package (optional opt-in) that sets `TenantId` from the SA's namespace.

### Out-of-cluster fallback

When runtime sits **outside** the cluster, `https://kubernetes.default.svc` isn't reachable. Operator falls back to static client-credentials JWT via existing v0.6 path. Config flag `Vais:KubernetesOperator:Auth:Mode = "service-account" | "client-credentials"`.

### Decision (Q3): **projected SA token + `DelegatingHandler` on operator; standard `AddJwtBearer` with K8s OIDC discovery on runtime**

Zero runtime code changes — just config guidance in the operator's Helm chart + README. Operator ships `ServiceAccountTokenHandler` + optional `ServiceAccountPrincipalMapper` in the new package. Out-of-cluster fallback documented.

---

## Q4 — Reconcile loop, diff, status subresource, finalizer

### Reconcile decision table

| trigger | state | action |
|---|---|---|
| New CR (finalizer absent) | spec present, no status | Add finalizer `vais.io/agent-deactivate`. Set `status.phase = Pending`, `status.conditions = [{Ready: Unknown}]`. Requeue immediately. |
| CR with finalizer, no handle | `status.agentHandle == null` | Compute specHash. Call `CreateAsync(manifest)`. On success: store handle + revision + phase=Active + condition Ready=True. On failure: phase=Error, condition Ready=False + reason + message. Exponential-backoff requeue. |
| CR with finalizer + handle, spec hash matches | no change | Refresh `lastReconciledAt`. No runtime call. |
| CR with finalizer + handle, spec hash differs | spec changed | Call `UpdateAsync(handle, newManifest)`. On success: new handle (new version) + new revision + phase=Active. On failure: phase=Error + condition. |
| CR with `deletionTimestamp` set, `preserveOnDelete=false` | deleting | Phase=Terminating. Call `EvictAsync(handle)`. On success: remove finalizer → CR garbage-collected. On failure: condition with reason; retry with backoff. |
| CR with `deletionTimestamp` set, `preserveOnDelete=true` | deleting, preserve state | Phase=Terminating. Remove finalizer without calling runtime. CR garbage-collected. |

### AgentStatus shape

```csharp
public sealed record AgentStatus
{
    public AgentHandleRef? AgentHandle { get; init; }       // { agentId, version, instanceId? } mirror
    public string? ManifestRevision { get; init; }          // SHA-256 of canonical-JSON(spec) at last successful upsert
    public AgentPhase Phase { get; init; } = AgentPhase.Pending;
    public DateTimeOffset? LastReconciledAt { get; init; }
    public string? LastError { get; init; }                  // short exception type / message
    public IReadOnlyList<AgentCondition>? Conditions { get; init; }
    public long ObservedGeneration { get; init; }            // K8s pattern — `metadata.generation` seen
}

public enum AgentPhase { Pending, Creating, Active, Updating, Error, Terminating }

public sealed record AgentCondition(
    string Type,                    // "Ready" | "Synced" | "ManifestValid"
    string Status,                  // "True" | "False" | "Unknown"
    string Reason,                  // machine-readable, CamelCase
    string Message,                 // human-readable
    DateTimeOffset LastTransitionTime,
    long ObservedGeneration);
```

### Controller skeleton

```csharp
[EntityRbac(typeof(AgentEntity), Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1Secret), Verbs = RbacVerb.Get | RbacVerb.Watch | RbacVerb.List)]
[EntityRbac(typeof(Corev1Event), Verbs = RbacVerb.Create | RbacVerb.Patch)]
public sealed class AgentEntityController : IEntityController<AgentEntity>
{
    private readonly IAgentControlPlaneClient _client;
    private readonly IKubernetesClient _k8s;
    private readonly ILogger<AgentEntityController> _logger;
    // ...

    public async Task ReconcileAsync(AgentEntity entity, CancellationToken ct)
    {
        if (entity.Metadata.DeletionTimestamp is not null)
        {
            await HandleDeletionAsync(entity, ct);
            return;
        }
        await EnsureFinalizerAsync(entity, ct);
        var desiredSpecHash = ComputeSpecHash(entity.Spec);
        var existing = entity.Status?.AgentHandle;
        if (existing is null)
        {
            await CreateAndUpdateStatusAsync(entity, desiredSpecHash, ct);
        }
        else if (desiredSpecHash != entity.Status?.ManifestRevision)
        {
            await UpdateAndUpdateStatusAsync(entity, existing, desiredSpecHash, ct);
        }
        else
        {
            await TouchStatusAsync(entity, ct);
        }
    }

    public Task DeletedAsync(AgentEntity entity, CancellationToken ct) => Task.CompletedTask;
}
```

### Idempotency

Every operator → runtime call carries `Idempotency-Key = $"{cr.metadata.uid}:{cr.metadata.generation}:{verb}"`. This guarantees that reconcile retries after partial failure don't duplicate verb dispatch — the v0.11 idempotency store de-dupes on the server side.

### Decision (Q4): **hash-based diff + status subresource + `vais.io/agent-deactivate` finalizer + 3 conditions + operator-local phase enum + Idempotency-Key on every call**

Conditions = `Ready`, `Synced`, `ManifestValid`. Phase = `Pending | Creating | Active | Updating | Error | Terminating`. ObservedGeneration pattern matches K8s built-ins.

---

## Q5 — Secret resolution + CR shape

### `secretRefs` field on `AgentSpec`

```csharp
public sealed record AgentSpec(
    string AgentId,
    string Version,
    AgentHandlerRef Handler,
    IReadOnlyList<ProtocolBinding> Protocols,
    IReadOnlyList<ToolRef> Tools,
    // ... all other AgentManifest fields ...
    IReadOnlyDictionary<string, SecretKeyReference>? SecretRefs = null,
    bool PreserveOnDelete = false);

public sealed record SecretKeyReference(string Name, string Key);
```

YAML example:

```yaml
apiVersion: vais.io/v1alpha1
kind: Agent
metadata:
  name: chat-assistant
  namespace: default
  annotations:
    vais.io/tenant-id: tenant-42
spec:
  agentId: chat-assistant
  version: v1
  model: { provider: openai, modelId: gpt-4 }
  systemPrompt: { inline: "You are a helpful assistant." }
  tools:
    - { name: weather }
  mcpServers:
    - { name: weather-mcp, transport: stdio, command: "node", args: ["weather.js"] }
  secretRefs:
    OPENAI_API_KEY: { name: openai-creds, key: apiKey }
  preserveOnDelete: false
```

### Resolution flow

Operator's reconciler:

1. For each `secretRefs[key]` entry, `GET /api/v1/namespaces/<cr.ns>/secrets/<name>` → base64-decode value at `[key]`.
2. Build an `AgentManifest` by projecting `AgentSpec` → `AgentManifest`, with secret-resolver references replaced by resolved values.
3. Call `AgentControlPlaneClient.CreateAsync(manifest)` / `UpdateAsync(handle, manifest)`.

Resolved values ride the HTTPS wire. Runtime in-memory state matches current env-resolver behaviour (plain strings). No runtime-side K8s dependency.

**Audit sensitivity**: verified — `AuditLogEntry` captures only `{At, Operation, AgentId, AgentVersion, PrincipalId, TenantId, Allowed, DenyReason, ErrorType}`. No manifest body. Zero redaction work needed for v0.13.

### Secret re-resolution on Secret changes

Operator watches `V1Secret` resources in the namespaces it observes. On Secret change, it bumps a reconcile re-queue on any CR whose `secretRefs` points at the changed Secret. Triggers an `UpdateAsync` pass → runtime picks up rotated keys within one reconcile cycle.

**Tradeoff**: needs `get/watch/list` RBAC on `V1Secret` in the operator's SA. Scoped to observed namespaces (cluster-wide by default; `WATCH_NAMESPACES` env narrows).

### Decision (Q5): **`secretRefs: { [ref] → { name, key } }` on spec + operator resolves via K8s API before upsert + Secret-change re-resolution via watch**

Audit concerns dismissed — audit log doesn't touch manifest body. K8s-idiomatic muscle memory. Operator-side only; no runtime changes.

---

## Verdict — ready to write the pillar plan

### Locked decisions

1. **Single CRD**: `Agent` only. `AgentGraph` → v0.14; `AgentRun` → v0.15. Single-CRD pillar keeps scope tight and honest against current runtime surface.
2. **CRD type**: `[KubernetesEntity(Group = "vais.io", ApiVersion = "v1alpha1", Kind = "Agent")]` on `AgentEntity : CustomKubernetesEntity<AgentSpec, AgentStatus>`. Short names: `vagent`, `vagents`. Namespaced.
3. **`AgentSpec`**: record mirroring `AgentManifest` field-by-field + new `SecretRefs: IReadOnlyDictionary<string, SecretKeyReference>?` + `PreserveOnDelete: bool`.
4. **`AgentStatus`**: record with `{ AgentHandle, ManifestRevision, Phase, LastReconciledAt, LastError, Conditions[], ObservedGeneration }`. Phase enum = `Pending | Creating | Active | Updating | Error | Terminating`.
5. **Framework**: `KubeOps.Operator 9.x` metapackage on `net9.0`.
6. **Package split**: library `Vais.Agents.Control.KubernetesOperator` (CRD types + controller + DI extensions) + executable `Vais.Agents.Control.KubernetesOperator.Host` (container image entry point).
7. **Auth**: operator uses ServiceAccount-projected OIDC token via `DelegatingHandler` on `AgentControlPlaneClient`. Runtime validates via stock `AddJwtBearer` with K8s API as OIDC issuer. Out-of-cluster fallback via static client-credentials.
8. **Tenancy**: CRDs namespaced. `vais.io/tenant-id` annotation for explicit tenant binding. Optional `ServiceAccountPrincipalMapper` shipped in operator package maps SA namespace → TenantId.
9. **Reconcile**: hash-based diff + status subresource + `vais.io/agent-deactivate` finalizer + 3 conditions (`Ready`, `Synced`, `ManifestValid`) + `ObservedGeneration` pattern. Every call carries `Idempotency-Key = $"{uid}:{generation}:{verb}"`.
10. **Secrets**: `spec.secretRefs: { [ref] → { name, key } }`. Operator resolves via K8s API before upsert. Runtime sees plain values (same as env-resolver). Secret-change watch triggers re-resolution via `UpdateAsync`. No runtime changes. Audit log already excludes manifest body — zero redaction work.
11. **Helm chart**: `deploy/helm/vais-agents-operator/` ships Deployment + ServiceAccount + ClusterRole / ClusterRoleBinding + CRD install hook + projected-token volume. Helm-managed CRD install (no operator self-install).
12. **Out of scope / deferred**: `AgentGraph` CRD (→ v0.14), `AgentRun` CRD (→ v0.15), leader-election (single-replica MVP), in-process co-hosted mode, automated kind-in-CI (manual verification against user's `docker-desktop` K8s this round).

### Proposed PR shape (4 PRs)

**PR 1 — Package skeleton + CRD types.**
- New project `Vais.Agents.Control.KubernetesOperator` (library, targets `net9.0`, publishes NuGet).
- Deps: `KubeOps.Operator 9.x` (centralised in `Directory.Packages.props`).
- Types: `AgentEntity`, `AgentSpec`, `AgentStatus`, `AgentCondition`, `AgentPhase`, `AgentHandleRef`, `SecretKeyReference`.
- `[KubernetesEntity]` + `[KubernetesEntityShortNames]` + XML docs on all public types.
- Emit CRD YAML at `deploy/crds/vais.io_agents.yaml` (generated via `dotnet kubeops generate crds --output deploy/crds/`).
- PublicAPI baseline (`PublicAPI.Shipped.txt` + empty `Unshipped.txt`).
- Unit test project `Vais.Agents.Control.KubernetesOperator.Tests` + 1 smoke test (CRD type round-trips JSON shape matching `AgentManifest`).
- No controller yet.

**PR 2 — Controller + reconcile + secret resolver.**
- `AgentEntityController : IEntityController<AgentEntity>` with the full reconcile decision table (see Q4).
- `ServiceAccountTokenHandler : DelegatingHandler` + `ServiceAccountPrincipalMapper : IPrincipalMapper`.
- `KubernetesSecretResolver` class (internal) — resolves `secretRefs` via `IKubernetesClient.GetAsync<V1Secret>`.
- `AddAgentKubernetesOperator(IServiceCollection, Action<KubernetesOperatorOptions>?)` DI extension — wires controller + handlers + options.
- `KubernetesOperatorOptions` — `ControlPlaneBaseUrl`, `ControlPlaneAudience`, `TokenPath`, `AuthMode` ("service-account" | "client-credentials"), `WatchNamespaces`.
- Tests: ~10 unit tests with mocked `IAgentControlPlaneClient` + `IKubernetesClient`. Paths: create-new-CR, spec-changed, delete-with-preserve, delete-without-preserve, secret-ref resolution, idempotency-key composition, hash-based diff no-op.
- PublicAPI +~15 entries (DI extensions + options).

**PR 3 — Operator host + Helm chart + Dockerfile.**
- Executable project `Vais.Agents.Control.KubernetesOperator.Host` — `Program.cs` builds `Host`, wires `AddAgentKubernetesOperator()`, runs.
- Dockerfile (multi-stage: sdk → runtime; non-root user; `HEALTHCHECK`).
- Helm chart at `deploy/helm/vais-agents-operator/` — templates for Deployment, ServiceAccount, ClusterRole, ClusterRoleBinding (watches Agent CRs + Secrets), projected-volume config, values.yaml with control-plane URL + audience + auth mode. CRD install as Helm pre-install hook.
- Integration-ish test: spin up controller against a fake `AgentControlPlaneClient` + fake `IKubernetesClient`, apply a synthetic `AgentEntity`, assert the verb chain (CreateAsync called with resolved secrets, finalizer added, status populated).
- No tests against a real K8s cluster in CI — deferred as manual verification step against user's docker-desktop K8s.

**PR 4 — v0.13.0-preview cut.**
- API freeze on the new library package (promote Unshipped → Shipped).
- Pack 23 packages at `0.13.0-preview`.
- Smoketest bump + K8s-operator library-surface probe (construct an `AgentEntity`, verify `[KubernetesEntity]` attr metadata, round-trip `AgentSpec` JSON).
- Tag `v0.13.0-preview`.
- Milestone log entry + research doc §7 strike-through.
- Manual acceptance demo (optional, end-of-PR): user runs `helm install vais-agents-operator` against docker-desktop, applies sample CR, verifies operator logs + reconcile.

### Effort estimate

**4 PRs, ~3 days focused work.** PR 1 is small (types + skeleton); PR 2 is the largest (controller + tests); PR 3 is medium (Helm chart + Dockerfile + host); PR 4 is routine (freeze + pack + tag). KubeOps-onboarding papercuts likely add 3–4 hours.

### Non-goals for v0.13

- **AgentGraph CRD**. → v0.14 pillar (bundled with `IAgentGraphRegistry` + HTTP `POST /v1/graphs` verbs).
- **AgentRun CRD**. → v0.15 pillar (bundled with `IAgentRunRegistry` + `GET /v1/agents/{id}/runs/{runId}` verb).
- **Leader election / multi-replica**. Single-replica MVP; `Lease`-based HA when a real-cluster deployment surfaces need.
- **In-process co-hosted operator** (operator as `IHostedService` in the silo pod). Deferred until someone asks.
- **Automated kind-in-CI integration tests**. Controller logic covered by unit tests with mocks; cluster-side validation is manual against user's docker-desktop K8s for v0.13.
- **CRD schema JSON published as a standalone JSON-schema file**. The CRD YAML + KubeOps transpiler are sufficient; consumers who want an external JSON-schema can generate from the CRD.
- **Multi-version CR support** (e.g., `v1alpha1` + `v1beta1`). Single `v1alpha1` version; storage-version upgrade is a future concern.
- **Metrics / traces from the operator**. KubeOps 9.x emits default controller metrics; we don't wire custom ones this round.
- **Operator config hot-reload**. Config via env + CLI args; restart to reconfigure. Standard K8s operator pattern.
- **Audit-log redaction helpers**. `AuditLogEntry` doesn't capture manifest body — nothing to redact.

---

## Open items (for pillar planning, not blockers)

1. **KubeOps 9.x actual latest version**. Pin to whatever the current stable is when PR 1 lands — check NuGet at that moment. If `9.4.x+` is out, take it; if only `9.0.x` is available, that's fine.
2. **CustomKubernetesEntity<TSpec, TStatus> vs. hand-rolled class**. KubeOps ships both. `CustomKubernetesEntity<,>` is the lean path.
3. **`AgentPhase` enum serialisation** — PascalCase vs. kebab-case vs. camelCase on the wire. K8s convention = PascalCase for phases (matches `Pod.status.phase = Running`). Lean: PascalCase; set `[JsonStringEnumConverter(JsonNamingPolicy.Pascal)]`.
4. **SecretKeyReference JSON shape**. K8s `SecretKeySelector` is `{ name, key, optional }`. We omit `optional` for v0.13 — missing secret is an error (reconcile sets condition + requeues).
5. **Helm chart version numbering**. Tie to the image tag (`0.13.0-preview`). Helm `appVersion` = image tag; chart `version` = chart-schema version (SemVer, starting at `0.1.0`).
6. **Dockerfile base image**. `mcr.microsoft.com/dotnet/aspnet:9.0-alpine` for size; non-root USER; expose health port only.
7. **ClusterRole vs. Role**. ClusterRole needed for watching Agent CRs across namespaces. RoleBinding per namespace if we want tenant-namespace isolation; cluster-wide by default for v0.13.
8. **Secret-watch scope**. Cluster-wide vs. per-namespace. Lean: cluster-wide (simpler RBAC). Production deployments narrow via `WATCH_NAMESPACES`.
9. **Observability**. KubeOps built-in metrics + standard .NET `ILogger`. OpenTelemetry wiring optional via `AddOpenTelemetry()` in the host if operator users want traces — not in default Helm chart (dep minimisation).
10. **`kubectl explain` output quality**. XML docs on `AgentSpec` properties become OpenAPI `description` fields → surface in `kubectl explain vagent.spec.<field>`. Worth writing good XML docs in PR 1.
