# Kubernetes operator

Shipped in v0.13 as `Vais.Agents.Control.KubernetesOperator` (library) + `Vais.Agents.Control.KubernetesOperator.Host` (in-repo container exe) + Helm chart at `deploy/helm/vais-agents-operator/`. The operator wraps the v0.6 HTTP control plane in a declarative K8s custom resource so `kubectl apply -f agent.yaml` is a first-class way to deploy + update + evict agents.

Built on KubeOps 10.3.4. Reconciles via SHA-256 spec-hash diff + three standard K8s conditions (`Ready` / `Synced` / `ManifestValid`) + a six-state phase enum + `ObservedGeneration` convention + `Idempotency-Key` derived from CR `{uid, generation, verb}` so a reconcile loop replaying mid-flight won't double-create.

## Where it fits

```
┌──────────────────────┐      kubectl apply      ┌────────────────┐
│  kind: Agent YAML    │───────────────────────▶│  kube-apiserver│
└──────────────────────┘                         └────────┬───────┘
                                                          │ watch
                                                          ▼
                                ┌──────────────────────────────────────┐
                                │  vais-agents-operator pod            │
                                │  (IAgentControlPlaneClient HTTP →)   │
                                └──────────────────┬───────────────────┘
                                                   │ POST /v1/agents/{id}
                                                   │ Idempotency-Key: uid:gen:create
                                                   ▼
                                ┌──────────────────────────────────────┐
                                │  v0.6 HTTP control plane (your host) │
                                │  → IAgentLifecycleManager.CreateAsync│
                                └──────────────────────────────────────┘
```

The operator does **not** replace the control plane — it's an adapter that translates CR watch events into HTTP verbs against an existing control-plane deployment. A v0.11 `Idempotency-Key` stamps every call so reconcile loops are safe under retry + split-brain.

## The `Agent` CRD

One custom resource type, cluster-scoped registration + namespaced objects.

| Field | Value |
|---|---|
| API group | `vais.io` |
| API version | `v1alpha1` |
| Kind | `Agent` |
| Plural / singular | `agents` / `agent` |
| Short names | `vagent`, `vagents` |
| Namespaced? | Yes — one Agent CR per namespace per agent id. |

Minimal manifest:

```yaml
apiVersion: vais.io/v1alpha1
kind: Agent
metadata:
  name: weather-agent
  namespace: default
spec:
  agentId: weather
  version: "1.0"
  handler:
    typeName: MyApp.WeatherAgent
  protocols: []
  tools: []
```

Full spec + status fields in the [Agent CRD reference](../reference/agent-crd.md).

## The `AgentGraph` CRD (v0.19)

Added in v0.19 alongside the graph control-plane API. Identical operator machinery to `Agent` — spec-hash diff detection, six-phase enum, three conditions (`Ready` / `Synced` / `ManifestValid`), idempotency key from `{uid, generation, verb}`.

| Field | Value |
|---|---|
| API group | `vais.io` |
| API version | `v1alpha1` |
| Kind | `AgentGraph` |
| Plural / singular | `agentgraphs` / `agentgraph` |
| Short names | `vgraph`, `vgraphs` |
| Namespaced? | Yes |

Minimal manifest:

```yaml
apiVersion: vais.io/v1alpha1
kind: AgentGraph
metadata:
  name: my-pipeline
  namespace: default
spec:
  graphId: my-pipeline
  version: "1.0"
  entry: start
  nodes:
    - id: start
      kind: Agent
      ref:
        id: classifier
        version: "1.0"
    - id: done
      kind: End
  edges:
    - from: start
      to: done
```

The CRD manifest is at `deploy/crds/vais.io_agentgraphs.yaml`. Printer columns: `GRAPH-ID`, `VERSION`, `PHASE`, `READY`, `AGE`.

## Reconcile loop

`AgentEntityController : IEntityController<AgentEntity>` drives the loop. On every CR create / update / status-subresource poke, KubeOps invokes `ReconcileAsync` with the live CR. The controller:

1. **Validate the spec.** `KubernetesSecretResolver` checks every `secretRef` exists in the CR's namespace; `SpecHasher.Compute(AgentSpec)` produces the canonical SHA-256 hex digest; runtime-side validation (e.g. JSON-schema on `OutputSchema`) runs as part of `IAgentLifecycleManager.CreateAsync` / `UpdateAsync`.
2. **Decide the verb.** Compare `SpecHasher.Compute(spec)` against `status.manifestRevision`.
   - No status → `CreateAsync` (the agent hasn't been registered yet).
   - Hash match → no-op (reconciler bumps `ObservedGeneration` on the status, nothing else).
   - Hash mismatch → `UpdateAsync` (spec drift since last reconcile).
3. **Call the control plane.** `IAgentControlPlaneClient.CreateAsync` / `UpdateAsync` with `Idempotency-Key = $"{uid}:{generation}:{verb}"`. The v0.11 middleware de-duplicates within the 24h TTL — a restart-replay within that window finds the cached response, no double-registration.
4. **Write status.** Phase, conditions, `manifestRevision`, `agentHandle`, `observedGeneration`, `lastReconciledAt`. Any failure flips `Phase = Error` + sets `Ready = False` with a structured `Reason`.

Deletion is handled via a finalizer — `vais.io/agent-deactivate`. KubeOps adds it on every reconcile; `AgentEntityFinalizer.FinalizeAsync` runs when `metadata.deletionTimestamp` appears:

- `spec.preserveOnDelete = false` (default) → `EvictAsync` on the control plane, then remove finalizer.
- `spec.preserveOnDelete = true` → remove finalizer without calling `EvictAsync`. Agent state stays in the runtime; useful for re-bootstrap flows where the CR is re-created from a different source of truth.

## Spec hash (`manifestRevision`)

The spec-hash drives the Create-vs-Update decision. Without it, every reconcile would call `UpdateAsync` (which is idempotent thanks to v0.11 but still racks up audit noise + unnecessary runtime churn).

`SpecHasher.Compute(AgentSpec)` serialises the spec to **canonical JSON**:

- Web-case property names (`agentId`, not `AgentId`).
- Null values elided entirely (not serialised as `null`).
- Object keys sorted alphabetically, recursively.

Then SHA-256, lowercase hex, prefixed with `sha256:`. Example: `sha256:9f2d8c…`.

Stored on `status.manifestRevision`. On next reconcile, re-hash the live spec and compare strings. No "diff the fields" logic — one comparison, cheap + robust.

## `ObservedGeneration` convention

Standard K8s pattern. `metadata.generation` is API-server-maintained — bumps when spec changes, not when status changes. `status.observedGeneration` is operator-maintained — set to `metadata.generation` on every successful status write.

Consumers detect stale status by comparing:

```bash
kubectl get agent weather-agent -o jsonpath='{.metadata.generation} vs {.status.observedGeneration}'
# 3 vs 2   ← status is stale; reconciler hasn't caught up yet
# 3 vs 3   ← status reflects current spec
```

The `Age` + `Phase` printer columns are a quick eyeball; `observedGeneration` is the authoritative freshness check.

## Phase state machine

Six states, `AgentPhase` enum in `AgentStatus.Phase`:

| Value | Phase | Meaning |
|---|---|---|
| 0 | `Pending` | CR exists but hasn't been reconciled yet. Transient; usually < 1s. |
| 1 | `Creating` | `CreateAsync` call in flight. Transient. |
| 2 | `Active` | Runtime holds the agent; last reconcile succeeded. |
| 3 | `Updating` | `UpdateAsync` call in flight after spec drift. Transient. |
| 4 | `Error` | Last reconcile failed. Conditions explain why. Backoff before retry. |
| 5 | `Terminating` | `metadata.deletionTimestamp` set; finalizer running. |

Transitions:

```
Pending ─CreateAsync─▶ Creating ─success─▶ Active ─spec drift─▶ Updating ─success─▶ Active
                          │                   │                     │
                        fail              delete CR             fail
                          ▼                   ▼                     ▼
                        Error ◀────────── Terminating             Error
```

`Error` retries on every reconcile tick (KubeOps schedules a requeue on exception); the backoff curve is KubeOps-default (exponential up to 10 minutes).

## Three conditions

Standard K8s `status.conditions` shape. Every Agent CR exposes exactly three:

| Condition type | Active | Error (spec issue) | Error (operational) | Terminating |
|---|---|---|---|---|
| `Ready` | `True` / `ReconcileSucceeded` | `False` / `ManifestInvalid` or `SecretResolutionFailed` | `False` / `ReconcileFailed` | set last before eviction; `False` if eviction fails |
| `Synced` | `True` / `RuntimeMatchesSpec` | `Unknown` / reason mirrors `Ready` | `Unknown` / `ReconcileFailed` | — |
| `ManifestValid` | `True` / `ValidationPassed` | `False` / `ManifestInvalid` or `SecretResolutionFailed` | `Unknown` / `ReconcileFailed` | — |

- **`Ready`** — caller-facing "is this agent usable right now?" boolean. Dashboards + `kubectl wait --for=condition=Ready agent/weather-agent` hinge on this.
- **`Synced`** — "does the runtime reflect current spec?" Splits from `Ready` so operational failures (control plane unreachable) don't cross-contaminate manifest-validity signalling.
- **`ManifestValid`** — "is the spec itself a valid agent?" False on schema issues, missing secrets, circular handoffs. Unknown on operational failures we can't prove manifest validity from.

`Reason` strings are CamelCase + stable — consumers script against them. `Message` is free-form English for humans.

## `Idempotency-Key` from CR triple

Every outbound HTTP call carries `Idempotency-Key: {uid}:{generation}:{verb}`.

- `uid` — `metadata.uid`, stable across the CR's lifetime (API server assigns on create; unchanged by any subsequent edit).
- `generation` — `metadata.generation`, bumped by the API server on every spec change.
- `verb` — `create` / `update` / `evict`.

This 4-tuple ensures:

- Operator restart mid-`CreateAsync` → KubeOps replays the reconcile → same `Idempotency-Key` → v0.11 middleware serves the cached response. No double-registration.
- Spec change between reconciles → new `generation` → new key → new call. No accidental re-use across semantically different operations.
- Two different Agent CRs that happen to have the same `agentId` → different `uid` → different keys. No cross-CR collision.

Within the 24h TTL of the v0.11 idempotency store, any replay is safe.

## Projected SA token auth

The operator authenticates to the HTTP control plane via a projected `ServiceAccount` token — standard K8s pattern, no static secret:

- Pod spec mounts a `projected:` volume with a `serviceAccountToken:` source targeting `audience: vais-agents-runtime` (configurable via `auth.tokenPath` + `controlPlane.audience` in the Helm values).
- Token lives at `/var/run/secrets/tokens/vais-runtime-token` — rotated by kubelet every hour (default `tokenExpirationSeconds: 3600`).
- `ServiceAccountTokenHandler : DelegatingHandler` reads the token on every outbound request, caches for up to 1h or until the file `mtime` changes (whichever comes first), and injects as `Authorization: Bearer {token}`.

The control plane validates the token against whichever OIDC issuer your cluster's `TokenRequest` API configures (kube-apiserver's JWKS, or an external OIDC provider if one is configured via `--service-account-issuer`). Maps to an `AgentPrincipal` via `ServiceAccountPrincipalMapper` for the v0.14 policy engine.

## Helm chart

`deploy/helm/vais-agents-operator/` — chart `0.1.0`, appVersion `0.13.0-preview`, targets Kubernetes `>= 1.28`. Top-level values:

```yaml
image:
  repository: vais-agents-operator
  tag: 0.13.0-preview
  pullPolicy: IfNotPresent
replicaCount: 1                # single replica — no leader election in v0.13
controlPlane:
  baseUrl: https://vais-control-plane.example.svc
  audience: vais-agents-runtime
auth:
  mode: ServiceAccount
  tokenPath: /var/run/secrets/tokens/vais-runtime-token
  tokenExpirationSeconds: 3600
watchNamespaces: []            # empty = cluster-wide
rbac:
  create: true
  installCrd: true             # installs deploy/crds/vais.io_agents.yaml on helm install
resources:
  requests: { cpu: 50m, memory: 64Mi }
  limits:   { memory: 128Mi }
```

The chart ships CRD manifest at `deploy/crds/vais.io_agents.yaml` — mount via Helm's `pre-install` hook so the CRD lands before the operator pod starts. Upgrading CRDs across chart versions follows the standard K8s dance (`kubectl apply -f crds/` for breaking schema changes; Helm doesn't upgrade CRD definitions for safety).

## Limitations (v0.13)

- **Secret-refs are validation-only.** The operator resolves `spec.secretRefs` against the K8s API and flips `ManifestValid = False` on missing refs — but resolved values are **not** injected into the manifest envelope sent to the control plane. Authors use `env:` or `file:` URIs in manifest fields directly; the runtime's existing `ISecretResolver` composite picks those up at agent-instantiation time. Inline-value projection (`secret://inline/<name>` + runtime-side map resolver) lands in a later pillar.
- **Autoscaling + budget + other optional fields pass through; HPA is not wired.** The operator records `spec.autoscaling` on the manifest envelope but does not create an HPA in response. Same story for `Budget`, `Model`, `Identity` — projected field-by-field, acted on by the runtime if implemented there.
- **Single-replica only — no leader election.** The Helm chart defaults `replicaCount: 1`. Multi-replica deployments would double-reconcile; leader election lands with multi-region support.
- **CRD schema uses `x-kubernetes-preserve-unknown-fields: true`.** KubeOps 10.3.4's `kubeops generate operator` transpiler doesn't tolerate `TimeSpan` fields (`AutoscalingSpec.IdleTtl`, `RunBudget.MaxDuration`). The CRD is hand-written in `deploy/crds/vais.io_agents.yaml` and accepts any JSON object under the opaque sub-schemas; client-side validation via `kubectl explain` degrades to "here be dragons". Will be tightened when upstream adds TimeSpan support or we write a custom transpiler.

## See also

- [Deploy the Kubernetes operator](../guides/deploy-the-kubernetes-operator.md) — Docker Desktop quick-start, end-to-end.
- [Wire a sidecar OPA against the operator](../guides/wire-a-sidecar-opa-against-the-operator.md) — combined v0.13 + v0.14 policy deployment.
- [Agent CRD reference](../reference/agent-crd.md) — full schema, status fields, printer columns.
- [Graph as a first-class deployable](graph-as-deployable.md) — v0.19 graph manifest format + full management surface.
- [Control plane concept](control-plane.md) — the v0.6 HTTP surface the operator wraps.
- [Problem-details URNs](../reference/problem-details-urns.md) — error shapes the operator surfaces into `status.lastError`.
- `deploy/README.md` + `deploy/helm/vais-agents-operator/README.md` — in-repo deployment notes.
