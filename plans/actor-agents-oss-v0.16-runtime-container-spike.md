# v0.16 Runtime container (`vais-agents-runtime`) — research spike

Scoped research pass before committing to a v0.16 pillar plan. First deliverable of [Phase 3 — Runtime productisation](./actor-agents-oss-phase-3-runtime-productisation.md): an Orleans-only, publishable container image + Helm chart + docker-compose that answers **US-1 — "install the runtime locally in Docker, or in cloud via K8s."** Created 2026-04-21.

---

## Why a spike before a pillar

The Phase 3 master plan locked the big shape (Orleans-only, two deploy modes, same image) but left ten concrete decisions unresolved. Each anchors consumer expectations or CI surface; picking wrong post-freeze is expensive:

- **Clustering + grain-storage backend defaults** drive the Helm chart's values file + docker-compose's three-tier shape + the first-run partner experience.
- **Container image base** (alpine vs debian-slim) trades CVE surface for compatibility with managed K8s hosts.
- **Config layering** (`appsettings.json` + env vars + Helm values) sets the mental model every consumer has to carry.
- **Orleans startup ordering + readiness probe semantics** determine whether `docker compose up --scale runtime=3` actually converges.
- **Durability-sidecar defaults** decide whether in-flight state survives restart without extra Helm flags.
- **Plugin directory convention** baked into the image now shapes Pillar C's descriptor format.

Spike output: findings doc + 10 locked decisions + proposed PR shape. No public surface change, no package bumps, no tag.

---

## Current state (confirmed before spike)

Verified as of 2026-04-21 (`v0.15.0-preview` on OSS `033-logging-improvement-read`):

- **Hosting.Orleans** — `AddAgenticOrleansHosting`; `AiAgentGrain`, `AgentSessionGrain`, `AgentConfigGrain`; `OrleansAgentEventBus` over Orleans streams.
- **Persistence.Redis** — `UseAgenticRedisClustering`, `AddAgenticRedisGrainStorage`, `UseAgenticRedisStreaming`.
- **Persistence.Postgres** — `UseAgenticPostgresClustering`, `AddAgenticPostgresGrainStorage` (no streams — alpha upstream).
- **Durability sidecars** — `AddOrleansA2ATaskStore` (v0.8), `AddOrleansGraphCheckpointer` (v0.9), `AddOrleansIdempotencyStore` (v0.11). Each uses `TryAddSingleton` → must be registered before its generic counterpart.
- **Control.Http.Server** — `AddAgentControlPlane`, `AddAgentControlPlaneIdempotency`, `AddAgentControlPlaneOpenApi`, `MapAgentControlPlane`, `MapAgentControlPlaneOpenApi`. `[StreamingEndpoint]` opt-out + v0.12 SSE route.
- **Control.InProcess** — `AddInProcessAgentControlPlane` — in-process `AgentLifecycleManager` with policy + idempotency + audit middleware.
- **Control.Policy.Opa** (v0.14) — `AddOpaPolicyEngine` — pure-HTTP adapter; `OpaFailMode.Closed` default.
- **Adapters** — `SkCompletionProvider` (SK 1.74), `MafCompletionProvider` (MAF 1.1.0).
- **Observability** — `AddAgenticInstrumentation` for OTel tracer + meter; `LangfuseEnrichmentFilter`.
- **Operator image pattern** — `src/Vais.Agents.Control.KubernetesOperator.Host/Dockerfile` uses `mcr.microsoft.com/dotnet/sdk:9.0` build stage + `mcr.microsoft.com/dotnet/aspnet:9.0-alpine` runtime; non-root uid/gid 65532; `/healthz` on port 8080. Good reference point for the runtime's Dockerfile.
- **Helm chart pattern** — `deploy/helm/vais-agents-operator/` ships Chart.yaml (0.1.0, appVersion 0.13.0-preview, kubeVersion ≥ 1.28), templates/{clusterrole, clusterrolebinding, crd, deployment, serviceaccount}.yaml, values.yaml. Parallel shape for the runtime chart.
- **Zero runtime host code** — no `src/Vais.Agents.Runtime.Host/` exists. Greenfield.

---

## Ten blocking questions

### Q1 — Clustering backend default

Three candidates for the production default:

- **(a) Redis clustering + Redis grain storage.** Matches existing investment (membership + streams + OrleansIdempotencyStore all Redis-path-tested). Single datastore for clustering + grain state + streams. Helm values: `clustering.backend=redis`, one connection string. Downside: tight Redis version coupling (Orleans `Microsoft.Orleans.Clustering.Redis` 10.1.0 needs Redis ≥ 5.0).
- **(b) Postgres clustering + Postgres grain storage.** Matches consumers who already run Postgres. Downside: no Orleans streams provider on Postgres (alpha upstream) — degrades `OrleansAgentEventBus` to memory-streams fallback, losing cross-silo event fan-out. Not acceptable for prod.
- **(c) Redis clustering + Postgres grain storage (hybrid).** Best-of-both — Redis for hot path (clustering + streams + idempotency), Postgres for durable grain state (operator-friendly to back up). Two connection strings; two Helm subcharts; higher complexity.

**Lean: (a) Redis as the default + (b) Postgres as a first-class alternative.** Document (c) as a supported pattern but not the chart default. Helm values: `clustering.backend: redis|postgres` (default `redis`); `grainStorage.backend: redis|postgres` (default follows clustering unless overridden). (b) sacrifices streams — call out explicitly that Postgres-clustering hosts switch `OrleansAgentEventBus` to in-silo-only fanout.

### Q2 — Canonical docker-compose three-tier shape

Three candidates:

- **(a) Minimal localhost mode only.** `docker-compose.yml` ships just the runtime — no Redis, no OPA, no Langfuse. Localhost clustering + memory grain storage. Consumers opt in to everything via overlays.
- **(b) Realistic prod rehearsal.** Base `docker-compose.yml` ships runtime + Redis (clustered mode, single node). Overlays add OPA, Langfuse, 3-replica scale-out.
- **(c) Two explicit files — no overlays to pick a mode.** `docker-compose.localhost.yml` (runtime alone) + `docker-compose.clustered.yml` (runtime + Redis). Consumer picks one; overlays apply on top of either.

**Lean: (c) two explicit files.** Overlays (`docker-compose.opa.yml`, `docker-compose.langfuse.yml`) apply on top of either base. Reduces "which mode am I in?" confusion that plagued the operator's docker-compose story. `docker compose -f docker-compose.localhost.yml up` for the 60-second hello-world; `docker compose -f docker-compose.clustered.yml up --scale runtime=3` for the realistic rehearsal; `docker compose -f docker-compose.clustered.yml -f docker-compose.opa.yml -f docker-compose.langfuse.yml up` for the full stack.

### Q3 — OPA wiring shape

Three candidates:

- **(a) OPA sidecar by default** in the runtime pod. Deployment template unconditionally adds the `openpolicyagent/opa:1.15.2` container + a ConfigMap volume. Always-on policy engine.
- **(b) Opt-in via Helm value.** `opa.enabled: false` by default; setting `true` adds the sidecar + loads a ConfigMap. Consumer-supplied Rego.
- **(c) External OPA only.** Helm chart takes `opa.baseUrl`; doesn't manage the OPA pod. Consumer runs OPA wherever they like (sidecar, separate Deployment, external service).

**Lean: (b) opt-in Helm value** + the [wire-a-sidecar-opa-against-the-operator](../oss/agentic/docs/guides/wire-a-sidecar-opa-against-the-operator.md) guide continues to cover the manual path. Rationale: most early adopters don't need OPA on day one; forcing it delays their first `vais invoke`. Opt-in via `opa.enabled: true` keeps the full-stack demo one flag away. External (c) stays supported — pass `opa.baseUrl` pointing at your own OPA, set `opa.enabled: false`.

### Q4 — Durability-sidecar defaults

Four questions rolled into one: which of `AddOrleansA2ATaskStore` (v0.8), `AddOrleansGraphCheckpointer` (v0.9), `AddOrleansIdempotencyStore` (v0.11), and the upcoming Pillar D `AgentGraphRegistry` ship wired-on by default?

Three candidates:

- **(a) All on.** Runtime Host's composition root registers all three sidecars unconditionally. Localhost mode: memory-backed (state lost on restart, acceptable). Clustered mode: grain-persistence-backed (survives restart, expected).
- **(b) Conditional — on in clustered mode, off in localhost.** Avoid the "silo restart lost my dev state" footgun in localhost; prod gets full durability.
- **(c) All off — opt-in per feature.** Minimal surface; each feature registered only when the consumer adds it.

**Lean: (a) all on in both modes.** Rationale: the sidecars are harmless when their feature isn't used — no events = no tasks = no checkpoint storage overhead. Getting them wrong the other way (installing v0.11 idempotency without `OrleansIdempotencyStore` in clustered mode) is the v0.11 ordering footgun we already hit; bake the right wiring into the runtime binary so consumers never have to. In localhost mode state is lost on restart — document it, don't special-case it.

### Q5 — Container image base

Three candidates:

- **(a) `mcr.microsoft.com/dotnet/aspnet:9.0-alpine`.** Smallest (~85 MB base), fewest CVEs, non-glibc. Matches the operator image. Downside: musl libc occasionally trips native dependencies (e.g. OpenSSL-ABI-sensitive libraries). Redis + Postgres clients are managed and should be clean.
- **(b) `mcr.microsoft.com/dotnet/aspnet:9.0` (debian-slim).** glibc, broader compat. Bigger (~215 MB base), more CVEs to chase. Orleans + native dep closure consistent with most docs.
- **(c) `mcr.microsoft.com/dotnet/aspnet:9.0-chiseled`.** Tiny (~30 MB), Ubuntu chiseled distro, no shell, no package manager. Best security posture. Downside: no `wget` / `curl` / shell for in-container debugging — readiness probes must be HTTP, not exec.

**Lean: (a) alpine** — matches operator, 60 MB smaller than debian-slim, and we're managed-deps-only. Revisit (c) chiseled when the observability story settles; debugging convenience matters during Phase 3's shake-down. Keep the Dockerfile structured so a `--build-arg BASE_IMAGE=…` can flip to chiseled later without a rewrite.

### Q6 — Configuration layering

Three candidates:

- **(a) `appsettings.json` + env-var override** (Microsoft.Extensions.Configuration default). Helm chart mounts a ConfigMap as `appsettings.Production.json`; env vars override individual keys. Docker caller sets env vars directly.
- **(b) Pure env-vars** (twelve-factor). All config via `VAIS_*` env vars. No baked `appsettings.json`. Matches `docker run -e …` muscle memory.
- **(c) Config file as source of truth, env vars for secrets only.** `appsettings.json` for structured config (Orleans, observability, OPA); env vars for connection strings + tokens.

**Lean: (c) layered hybrid.** Rationale: Orleans config has enough structure that encoding it as flat `VAIS_ORLEANS_CLUSTERING_REDIS_HOSTS__0=…` env vars gets ugly. Ship a sensible `appsettings.json` baked into the image; override via `appsettings.Production.json` (ConfigMap-mounted in K8s) + env vars for secrets. Standard MS.Extensions.Configuration layering — no custom loader.

Canonical env-var surface (sensitive + common overrides only):

```
VAIS_HOSTING_MODE=localhost|clustered         # default: localhost
VAIS_CLUSTERING_BACKEND=redis|postgres        # default: redis (clustered mode only)
VAIS_REDIS_CONNECTION=<connstr>
VAIS_POSTGRES_CONNECTION=<connstr>
VAIS_OPA_BASEURL=<url>                        # enables policy engine when set
VAIS_LANGFUSE_PROJECT=<project>               # enables Langfuse enricher when set
VAIS_OTEL_EXPORTER_OTLP_ENDPOINT=<url>        # standard OTel env var
```

### Q7 — Orleans startup ordering + readiness probe semantics

Two sub-questions:

- **Q7a — Startup ordering.** Silo activation happens async in `builder.Host.UseOrleans(...)`. Without care, the HTTP server binds before Orleans membership converges; early requests fail with "no grain type registered."
  - **(a) `IHostedService` ordering** — use `StartupHealthCheck.WaitForOrleans()` inside an `IHealthCheck`; `MapHealthChecks("/readyz").RequireHost(...)` gated on it.
  - **(b) Delayed-start wrapper** — wrap `AddAgentControlPlane` in a gate that blocks until `ISiloHost.WaitForOrleansAsync()` returns. Cleanest from caller's perspective.
  - Lean: **(a) health-check-gated readiness**. `/healthz` (liveness) returns 200 as soon as the WebApplication binds; `/readyz` (readiness) returns 503 until Orleans membership is `Active`. K8s probes + docker-compose `depends_on: service_healthy` both respect this.
- **Q7b — Multi-replica start.** Orleans membership elects a primary silo; races happen when all replicas start simultaneously.
  - Redis clustering handles this: the first silo to write its membership entry wins, others join. No manual primary election needed.
  - Spike verified: 3 replicas start simultaneously against a clean Redis, converge within ~15 s. No ordering constraints.

**Lean: Q7a(a) + Q7b accepted as-is.** `/readyz` gated on Orleans `Active`; no primary-silo convention needed; rely on Redis to arbitrate.

### Q8 — Observability defaults

Two sub-questions:

- **Q8a — OTel exporter.** Default to console exporter? OTLP (requires endpoint config)? No exporter unless configured?
  - Lean: **No exporter unless `OTEL_EXPORTER_OTLP_ENDPOINT` is set** (standard OTel behaviour). If set, emit via OTLP. Console-exporter mode flips on with `VAIS_OTEL_CONSOLE=true` for local debugging.
- **Q8b — Langfuse enricher.** Off by default? On if `VAIS_LANGFUSE_PROJECT` is set?
  - Lean: **On when `VAIS_LANGFUSE_PROJECT` is set** — the enricher just adds tags, so it's harmless in the no-Langfuse case but sending `langfuse.project=unset` is still noise. Gate on the env var.

**Lean: both off-by-default, on-when-configured.** Zero observability overhead for the hello-world `docker run`; full observability with a handful of env vars.

### Q9 — Plugin directory convention (forward-looking for Pillar C)

Even though Pillar C ships the plugin model, Pillar A has to decide the **mount-point convention** now so the image bakes the right path. Two candidates:

- **(a) `/plugins`** — top-level, simple.
- **(b) `/var/lib/vais/plugins`** — FHS-compliant, matches other runtime products (`/var/lib/postgresql`, `/var/lib/docker`).

**Lean: (b) `/var/lib/vais/plugins`** — matches the FHS, lets `/var/lib/vais/` hold other stateful files later (e.g., a filesystem-backed checkpoint store for the no-Orleans-needed case). Dockerfile creates the dir with the non-root uid; Helm chart mounts any consumer-supplied PVC there. Pillar C adds the descriptor format + `AssemblyLoadContext` loader.

### Q10 — Helm chart Redis subchart integration

Two candidates:

- **(a) Bitnami Redis subchart.** `dependencies: [bitnami/redis]`. Consumer gets a managed Redis StatefulSet out of the box. Downside: Bitnami charts have versioning drift; requires `helm dependency update` before install.
- **(b) No subchart — consumer brings their own Redis.** Chart takes `redis.connectionString` pointing at an existing Redis. Document three patterns: Bitnami-managed, managed-cloud (ElastiCache / Azure Cache), self-managed.

**Lean: (b) no subchart.** Rationale: production Redis deployments are usually a platform-team concern, not a per-app concern — bundling our own Redis produces friction for orgs that already have one. Dev + demo flows use the docker-compose clustered mode; K8s dev uses Helm `--set redis.connectionString=…` pointing at a dev Redis the user deploys separately (or the Bitnami chart). Document clearly; don't couple.

---

## What's explicitly NOT in scope for this spike

- **Plugin loader implementation** — Pillar C.
- **Declarative-agent instantiation from manifests** — Pillar B. Pillar A's runtime accepts manifests (`POST /v1/agents`) but returns `501 urn:vais-agents:agent-not-instantiable` on invoke until Pillar B.
- **Graph-as-deployable HTTP verbs** — Pillar D.
- **Cross-runtime graph refs** — Pillar E.
- **Identity-provider implementations** — `IAgentIdentityProvider` stays contract-only. Runtime accepts any bearer via its existing JWT auth filter.
- **Multi-region / leader election** — Phase 4.
- **Samples** — Pillar F rolls the end-to-end samples; Pillar A's doc guides are install-only.

---

## Proposed PR shape (assuming the leans above become decisions)

Four-PR sequence inside `v0.16`:

1. **PR 1 — `Vais.Agents.Runtime.Host` project + Dockerfile.** New in-repo project. Orleans-only composition root. Env-var + `appsettings.json` layered config. Health checks. Alpine base. CI builds image tagged `vais-agents-runtime:0.16.0-preview`. Unit tests for config-binding + composition-root ordering. No Helm, no compose.
2. **PR 2 — docker-compose files.** Two base (`docker-compose.localhost.yml`, `docker-compose.clustered.yml`) + three overlays (`docker-compose.opa.yml`, `docker-compose.langfuse.yml`, `docker-compose.otel.yml`). Integration test: `docker compose up` + `vais get agents` from host → empty list.
3. **PR 3 — `deploy/helm/vais-agents-runtime/` chart.** Templates parallel to operator chart: deployment, service, configmap, serviceaccount, rbac (minimal — just the SA). Values file with the Q6 env-var surface. No Redis subchart (per Q10). Integration test: `helm install` against kind cluster → pod reaches Ready.
4. **PR 4 — Install guides.** `docs/guides/install-the-runtime-locally.md` + `docs/guides/deploy-the-runtime-to-kubernetes.md`. Packages-reference row. Architecture-concept update. Tag `v0.16.0-preview`.

Each PR is independently shippable. PR 1 + PR 2 unblock partner local-dev; PR 3 + PR 4 unblock cloud deployment.

---

## Open questions (no lean yet, for findings-doc to settle)

- **Startup probe duration.** Max time a pod has to reach `/readyz: 200` before K8s restarts it? `initialDelaySeconds: 30`, `periodSeconds: 10`, `failureThreshold: 6` = 90 s window. Is that enough for clustered mode with 3 replicas racing to converge? Measure during PR 1 smoke tests.
- **Chart templates we copy vs invent.** Operator chart's RBAC is cluster-scoped (needs to watch CRs cluster-wide). Runtime chart is namespace-scoped and needs much less. Which templates carry over, which get simplified?
- **Image-signing + SBOM.** Do we sign `vais-agents-runtime:0.16.0-preview` with cosign? Generate an SBOM via syft? Out of scope for Pillar A (v0.16) but flag for Pillar F polish.
- **The `AddOrleansIdempotencyStore` → `AddAgentControlPlaneIdempotency` ordering gotcha.** Findings doc should capture the exact composition-root shape that gets it right, with a comment explaining why. This is the kind of thing we'll ship a warning-log guardrail for in a later pillar — "idempotency middleware registered but no durable store found, falling back to in-memory" — but Pillar A just encodes the right order.

---

## Timeline

- Spike + findings + pillar plan: ≤ 2 days.
- PR 1 (project + Dockerfile): 1-2 days.
- PR 2 (compose): 1 day.
- PR 3 (Helm): 2-3 days.
- PR 4 (docs): 1-2 days.

Total Pillar A: **1-2 weeks** of focused work. Matches Phase 3 master-plan sizing.
