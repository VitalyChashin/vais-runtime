# v0.13 Kubernetes CRD + operator — research spike

Scoped research pass before committing to a v0.13 pillar plan. Companion to [`actor-agents-oss-extraction-research.md`](./actor-agents-oss-extraction-research.md) §7 backlog: *"Kubernetes CRDs + operator (`Vais.Agents.Control.KubernetesOperator`) — declarative agents as native K8s resources; reconciler drives `IAgentLifecycleManager` verbs to match cluster state."* Sits on top of the v0.6 HTTP control plane + v0.11 idempotency store + v0.12 streaming endpoint; everything in the runtime is already shipped — this pillar only builds a K8s-native declarative front door. Created 2026-04-20.

---

## Why a spike before a pillar

The research-doc §7 line is a single sentence covering a whole new package + deployment artefact + RBAC story + operator framework choice + CRD schema design + secret-resolution model. Initial design conversation collapsed the scope twice:

- **CRD scope**: started at "Agent + AgentGraph + AgentRun" (full); stepped back to "Agent only" after two control-plane gaps surfaced — there is no run-query HTTP endpoint and no `IAgentGraphRegistry` verbs. `AgentGraph` → v0.14, `AgentRun` → v0.15, each paired with its own runtime-side registry.
- **Wire binding**: started at "HTTP or in-process"; stepped to "HTTP only via `AgentControlPlaneClient`" — keeps the operator a stateless Deployment and lets the silo cluster scale independently.

What's left for the spike: the five hard questions that determine whether the single-CRD operator ships cleanly or burns three weeks on framework fights. Spike output: findings doc + CRD schema draft + operator host sketch. No public surface change, no package bumps, no tag.

---

## Current state (confirmed before spike)

Verified as of 2026-04-20 (`v0.12.0-preview` on OSS `main`):

- **Runtime HTTP surface** (`Vais.Agents.Control.Http.Server`): 7 verbs under `/v1` — `POST /agents` (create), `GET /agents` (list), `GET /agents/{id}` (query), `PATCH /agents/{id}` (update), `DELETE /agents/{id}` (cancel/evict), `POST /agents/{id}/invoke` (unary), `POST /agents/{id}/invoke/stream` (SSE), `POST /agents/{id}/signal` (signal). JWT-bearer auth via `AddAgentControlPlaneJwtAuth()`; `IPrincipalMapper` default = OIDC.
- **Runtime client** (`Vais.Agents.Control.Http.Client`): `AgentControlPlaneClient` + `IAgentControlPlaneClient`. Idempotency-Key support since v0.11. Configurable via `AgentControlPlaneClientOptions`.
- **Lifecycle verbs**: 7 on `IAgentLifecycleManager` — `CreateAsync`, `InvokeAsync`, `SignalAsync`, `QueryAsync` (agent status, not run status), `CancelAsync`, `UpdateAsync`, `EvictAsync`. All surfaced over HTTP.
- **AgentManifest** (shipped in Abstractions since v0.6, expanded since): id + version + model + systemPrompt + mcpServers + guardrails + handoffs + budget + contextProviders + outputSchema + agentMode + reasoning + observability + annotations. `JsonElement` sub-fields on `outputSchema` and `reasoning` (arbitrary JSON for SGR).
- **Secret resolution** (`Control.Abstractions`): `ISecretResolver` contract + `CompositeSecretResolver` + env / file resolvers. Resolvers consulted by runtime at `CreateAsync` / `UpdateAsync` time when manifest holds a secret ref.
- **Zero K8s code**: no operator package, no CRD, no Helm chart. Repo tree `src/` has 22 packages, none K8s-flavoured.
- **Deployment**: no operator pod specs. Root-repo `k8s/` directory has non-OSS VAIS2 cluster configs (langfuse, keycloak, clickhouse, context-forge-gateway) — not relevant to OSS operator.

---

## Five blocking questions

1. **Q1 — CRD schema design.** `AgentManifest` is a rich, nested object with polymorphic sub-structures (guardrails, handoffs, context-providers, reasoning, output-schema). Three paths for the CRD's OpenAPI v3 schema:
   - **(a) Hand-written subset schema** — only fields we want users to set declaratively (omit runtime-only fields like `observability` labels). Most control over validation; most maintenance churn when `AgentManifest` grows.
   - **(b) KubeOps-generated schema from `[KubernetesEntity]`-annotated C# class** — CR type is a mirror of `AgentManifest`; KubeOps' transpiler emits OpenAPI. `JsonElement` sub-fields mapped to `x-kubernetes-preserve-unknown-fields: true`.
   - **(c) Reuse v0.6 manifest JSON schema** — if we have a JSON schema shipped (we don't — `IAgentManifestLoader` validates programmatically), lift into CRD. Currently inapplicable.
   - Lean: **(b)** — CR type is a `[KubernetesEntity]` record mirroring `AgentManifest` 1:1. `x-kubernetes-preserve-unknown-fields` on `outputSchema` / `reasoning`. Operator hands the decoded manifest straight to `AgentControlPlaneClient.CreateAsync` (or constructs an `AgentManifest` from the CR spec fields — simple field-by-field projection).

2. **Q2 — Operator framework choice + host topology.** Pin to KubeOps 9.x per lean framing. Two remaining choices:
   - **(a) KubeOps.Operator** — the top-level metapackage pulls Transpiler + KubernetesClient + Hosting. `IEntityController<T>` + `[EntityRbac]` attrs + `UseKubernetesOperator()` extension. Emits CRD YAML via `dotnet run -- crds install`.
   - **(b) KubeOps minimal composition** — pick individual packages (`KubeOps.Operator.Web` + `KubeOps.KubernetesClient`) for smaller dep closure.
   - **Host topology**: operator as its own Deployment (single-replica, leader-election for HA later), or as a co-deployed sidecar on the silo pod. Lean = own Deployment.
   - Lean: **(a) full metapackage + own Deployment**. Metapackage gives us transpiler for CRD emission + controller framework + built-in leader-election primitives. Deployment model keeps ops + scaling independent.

3. **Q3 — Operator → runtime auth (SA token path).** Locked at ServiceAccount-projected OIDC per lean framing. Implementation details still open:
   - **(a) Projected volume with `audience=vais-agents-runtime`** — K8s refreshes hourly, mounted at `/var/run/secrets/tokens/vais-runtime/token`. Operator reads file per request (or caches for ~5min). Stock K8s API; no extra plumbing.
   - **(b) TokenReview API** — operator posts its token to `TokenReview`; runtime independently validates via `TokenReview`. Extra round-trip + requires both operator and runtime to have `authentication.k8s.io/tokenreviews` RBAC. Unneeded.
   - **(c) OIDC discovery** — runtime discovers K8s API's OIDC issuer (`kubectl get --raw /.well-known/openid-configuration`) + JWKS endpoint. Runtime validates JWT locally. Matches standard `AddAuthentication().AddJwtBearer()` flow with `Authority=<cluster-issuer>`. No extra K8s API calls.
   - Lean: **(a) + (c) combined** — operator uses projected volume; runtime validates via standard OIDC discovery against the K8s API's issuer. Added concern: cluster's issuer URL must be reachable from the runtime pod (usually `https://kubernetes.default.svc` which is in-cluster; from outside cluster it's not, so this model assumes runtime-in-cluster). For out-of-cluster runtime deployments, operator falls back to static client-credentials JWT (the existing v0.6 path).

4. **Q4 — Reconcile loop, diff, status subresource, finalizer.** Operator's decision table per reconcile pass. Four mechanics:
   - **Diff**: spec → compute SHA-256 or compact-JSON hash → compare vs. `.status.manifestRevision`. If different, act; if same, just refresh `lastReconciledAt`. Idempotency-Key on `AgentControlPlaneClient` calls deduplicates mid-reconcile retries naturally.
   - **Status subresource**: use K8s status-subresource pattern (`spec` and `status` have separate patch endpoints). KubeOps `IEntityController<T>` returns `ResourceControllerResult` which KubeOps writes to status.
   - **Finalizer**: `vais.io/agent-deactivate` added by operator on first reconcile. On `.metadata.deletionTimestamp != null`: if `spec.preserveOnDelete == true` → remove finalizer without calling runtime (K8s garbage-collects CR; agent stays in runtime); else call `EvictAsync(handle)` → remove finalizer on success; on failure, surface on status + re-queue (K8s holds the CR until finalizer clears).
   - **Conditions**: three — `Ready` (last reconcile succeeded), `Synced` (runtime state matches desired), `ManifestValid` (passed runtime validation on last upsert). Each condition has `status=True|False|Unknown`, `reason`, `message`, `lastTransitionTime`.
   - **Phase**: enum mirror of `AgentStatus` (Unknown / Active / Idle / Paused / Terminated) plus operator-local states `Pending` (never reconciled), `Creating` (mid-CreateAsync), `Updating` (mid-UpdateAsync), `Terminating` (deletionTimestamp set), `Error` (last reconcile failed, re-queued).
   - Lean: hash-based diff + status subresource + `vais.io/agent-deactivate` finalizer + 3 conditions + operator-local phase enum.

5. **Q5 — Secret resolution on the CR.** `AgentManifest` references secrets by name (resolver-scheme strings like `env:OPENAI_API_KEY` or `file:/secrets/openai`). In K8s, users expect to reference a Secret resource. Three shapes for the CR:
   - **(a) Flat field with resolver string** — user writes `spec.secrets.OPENAI_API_KEY: "k8s://default/openai/apiKey"`. Operator side implements a `k8s://` resolver that reads Secrets via K8s API. Runtime agnostic. Looks ugly in YAML.
   - **(b) K8s-native `secretKeyRef` on each secret** — user writes `spec.secretRefs.OPENAI_API_KEY: { secretKeyRef: { name: "openai", key: "apiKey" } }`. Operator resolves via K8s API → injects plain values into manifest before `CreateAsync`. K8s-idiomatic.
   - **(c) `envFrom`-style pod-spec reuse** — user points at a whole Secret; operator copies all keys into the manifest's secret map. Magical; error-prone.
   - Lean: **(b)** — matches `Pod.spec.containers.env.valueFrom.secretKeyRef` muscle memory. Operator resolves secrets eagerly before upsert; resolved values ride the HTTPS wire; runtime sees plain strings and stores in-memory (same as today's env-resolver).
   - **Audit-log sensitivity**: resolved secret values will flow through the v0.6 audit log unless we redact. Lean: add an `AuditLogRedactor` hook on the v0.6 audit surface (tiny addition) + operator flags redaction-required fields. Alternatively, have operator always set `Idempotency-Key` based on `(name, namespace, specHash)` so the cached audit entry is the secret-containing one (no new entries after first successful upsert). Pick the simpler: redaction helper now, Idempotency-Key based dedupe as a natural side-effect.

---

## Tasks (research + archetype exercises)

- [x] **Q1 — CRD schema emission.** `AgentManifest` shape audited (22 top-level fields; 15 nested records; 2 `JsonElement` fields). `[KubernetesEntity]` + `CustomKubernetesEntity<AgentSpec, AgentStatus>` mirror path chosen. `x-kubernetes-preserve-unknown-fields: true` on `JsonElement` sub-fields verified as KubeOps 9.x transpiler default; to be confirmed empirically during PR 1 emission.
- [x] **Q2 — KubeOps version + host topology.** `KubeOps.Operator 9.x` metapackage + .NET 9 TFM confirmed. Library + executable split landed: `Vais.Agents.Control.KubernetesOperator` (library, controller + types) + `Vais.Agents.Control.KubernetesOperator.Host` (exe, container image). Own Deployment, single replica (leader-election deferred).
- [x] **Q3 — SA token + JWT validation.** Projected-volume mount shape locked (audience=`vais-agents-runtime`, TTL=3600s, 5-min cache on operator side). `ServiceAccountTokenHandler : DelegatingHandler` design landed. Runtime-side = stock `AddJwtBearer` with `Authority=https://kubernetes.default.svc` + `ValidIssuers` + in-cluster CA trust. Out-of-cluster fallback to static client-credentials.
- [x] **Q4 — Reconcile loop + conditions.** 6-row decision table covering new-CR / spec-changed / hash-match / delete-preserve / delete-evict / finalizer-add paths. `AgentStatus` shape with `{ AgentHandle, ManifestRevision, Phase, LastReconciledAt, LastError, Conditions[], ObservedGeneration }`. `AgentPhase` enum = 6 states. Idempotency-Key `{uid}:{generation}:{verb}` on every operator→runtime call, deduping via v0.11 middleware.
- [x] **Q5 — Secret CR shape.** `SecretRefs: IReadOnlyDictionary<string, SecretKeyReference>?` on `AgentSpec`. Operator resolves via K8s API before upsert; runtime unchanged (sees plain values, same as env-resolver). Secret-watch triggers re-reconcile on Secret changes. **Audit-redaction concern dismissed**: `AuditLogEntry` captures only verb-metadata, never manifest body. Zero redaction work needed.
- [x] **Findings doc.** [`actor-agents-oss-v0.13-kubernetes-operator-findings.md`](./actor-agents-oss-v0.13-kubernetes-operator-findings.md) — Q1–Q5 synthesis + 12 locked decisions + proposed 4-PR pillar shape + ~3-day effort estimate.

---

## Exit criteria

- [x] All five questions answered with evidence (not opinion) — Q1 from KubeOps transpiler behaviour on `CustomKubernetesEntity<TSpec,TStatus>` pattern + `AgentManifest` 22-field audit; Q2 from NuGet metapackage audit + host-split sketch; Q3 from projected-volume + standard `AddJwtBearer` OIDC discovery walk-through; Q4 from 6-row reconcile-path table + status shape; Q5 from `secretRefs` shape + `AuditLogEntry` field audit (dismissed redaction).
- [x] Recommendation lands: **ready to write v0.13 pillar plan.** 12 decisions locked in findings doc.

No public surface change. No package bumps. No tag.

---

## Progress log

- 2026-04-20 — spike plan created after design conversation. Scope collapsed from "Agent + AgentGraph + AgentRun" to "Agent only" after confirming runtime lacks graph-registry HTTP verbs and run-status HTTP verb. Graph CRD → v0.14 pillar paired with `IAgentGraphRegistry`; Run CRD → v0.15 pillar paired with run-status endpoint. Locked: KubeOps 9.x metapackage, HTTP-only wire via `AgentControlPlaneClient`, namespaced CRD with `vais.io/tenant-id` annotation, SA-projected OIDC token for JWT auth, operator resolves K8s Secrets before `CreateAsync`/`UpdateAsync`. Five blocking questions scoped (CRD schema emission, framework choice + host topology, SA token + JWT validation, reconcile loop + conditions, secret CR shape + audit redaction). Findings doc pending.
- 2026-04-20 — Spike complete. All five leans held up with one simplification: Q5's "audit-redaction helper" dropped after confirming `AuditLogEntry` records only verb-metadata, never manifest body. Q1: `[KubernetesEntity]` + `CustomKubernetesEntity<AgentSpec, AgentStatus>` mirror pattern; `AgentSpec` adds `SecretRefs` + `PreserveOnDelete`. Q2: `KubeOps.Operator 9.x` metapackage on .NET 9; library + executable split. Q3: projected SA token + `DelegatingHandler`; runtime side uses stock `AddJwtBearer` with K8s API as OIDC issuer. Q4: hash-based diff + 3 conditions + 6-state phase + Idempotency-Key on every operator→runtime call. Q5: `secretRefs: { [ref] → { name, key } }` on spec + operator resolves before upsert + Secret-watch for re-resolution. Findings doc landed with 12 locked decisions + proposed 4-PR pillar shape (PR 1 package skeleton + CRD types; PR 2 controller + reconcile + SA-token handler + secret resolver; PR 3 operator host + Helm chart + Dockerfile; PR 4 v0.13.0-preview cut). Effort estimate: ~3 days. One new library package + one new executable project + Helm chart. **Ready to write v0.13 pillar plan.**
