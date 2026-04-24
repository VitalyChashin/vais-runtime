# v0.16 Runtime container — spike findings

Synthesis of the research spike scoped in [`actor-agents-oss-v0.16-runtime-container-spike.md`](./actor-agents-oss-v0.16-runtime-container-spike.md). Answers Q1–Q10 + the four open sub-questions. Landing verdict at the bottom.

Created 2026-04-21. **Status**: complete. All ten blocking questions resolved; four open questions from the spike closed with evidence.

---

## Q1 — Clustering backend default

### Evidence

- **Redis clustering (`Microsoft.Orleans.Clustering.Redis 10.1.0`)** — mature, widely deployed, well-documented membership protocol. Single round-trip to elect membership. Pairs with Redis streams (`Microsoft.Orleans.Streaming.Redis`) + Redis grain storage in one connection string. v0.4 shipped against this and the path is battle-tested.
- **Postgres clustering (`Microsoft.Orleans.Clustering.AdoNet 10.1.0`)** — ADO.NET provider, production-grade. `OrleansPostgresMembership` table uses `SELECT FOR UPDATE` + heartbeat rows. Works fine, but the matching streams provider is alpha upstream — consumers of `OrleansAgentEventBus` on Postgres-clustered hosts get memory-streams fallback, which is in-silo only. Cross-silo event fanout silently degrades.
- **Hybrid (Redis clustering + Postgres grain storage)** — supported composition. Used by teams that want Redis hot-path + Postgres durable state. More config; more moving parts.

### Decision matrix

| Situation | Pick | Why |
|---|---|---|
| No strong preference / greenfield | Redis | Single backend, streams work, matches our existing Redis-path investment |
| Org already runs Postgres, no Redis allowed | Postgres | Accept the streams degradation; document it |
| Org runs both, wants Postgres durability for operator-friendly backup | Hybrid | Redis for hot path, Postgres for grain state |

### Decision (Q1): **Redis is the chart default; Postgres is a first-class alternative; hybrid is documented but not chart-native**

Helm values:

```yaml
clustering:
  backend: redis              # default; "postgres" flips the full stack
  connectionString: ""        # required when backend is set
grainStorage:
  backend: ""                 # "" = follow clustering.backend; "redis" / "postgres" to split
  connectionString: ""        # used when grainStorage.backend differs from clustering.backend
```

Validation at chart template time: error if `clustering.backend=postgres` + consumer hasn't set `clustering.connectionString`. Streams degradation warning emitted from the runtime binary on startup when Postgres clustering is active.

---

## Q2 — Canonical docker-compose three-tier shape

### Evidence — operator chart precedent

`deploy/helm/vais-agents-operator/` already ships a single `values.yaml` with mode-switching (`watchNamespaces: []` vs. explicit list). Consumers found it confusing per the v0.13 findings. Moving away from overlay-driven mode-switching to explicit base files is a deliberate simplification.

### Compose file inventory

Five files — two bases + three overlays:

```
deploy/compose/
├── docker-compose.localhost.yml      # base: runtime only (localhost clustering + memory grain)
├── docker-compose.clustered.yml      # base: runtime + Redis (clustered mode, 1 replica)
├── docker-compose.opa.yml            # overlay: adds OPA sidecar + ConfigMap mount
├── docker-compose.langfuse.yml       # overlay: adds Langfuse + Postgres for Langfuse
└── docker-compose.otel.yml           # overlay: adds Jaeger + Prometheus
```

Usage:

```bash
# 60-second hello-world
docker compose -f docker-compose.localhost.yml up

# Realistic prod rehearsal
docker compose -f docker-compose.clustered.yml up --scale runtime=3

# Full stack
docker compose -f docker-compose.clustered.yml \
               -f docker-compose.opa.yml \
               -f docker-compose.langfuse.yml up
```

### Decision (Q2): **Two explicit base files, three orthogonal overlays**

No `docker-compose.yml` symlink — consumers pick their base explicitly. Eliminates the "which mode am I in?" confusion.

---

## Q3 — OPA wiring shape

### Evidence

- Day-one partner onboarding metric: partners want `vais invoke` to work within 5 minutes of pull. Requiring OPA sidecar deployment + ConfigMap + Rego before first invoke trips that budget.
- OPA is opt-in at the runtime-binary level already — `AddOpaPolicyEngine` is a separate extension call. Aligns with gating it at the chart level.
- The v0.13 + v0.14 combined guide (`wire-a-sidecar-opa-against-the-operator.md`) covers the manual sidecar-patch path and stays valid.

### Decision (Q3): **Helm `opa.enabled: false` default; external OPA via `opa.baseUrl` stays supported**

Helm values:

```yaml
opa:
  enabled: false              # flip true to add the sidecar container + ConfigMap volume
  baseUrl: ""                 # external OPA URL; ignored when enabled=true (loopback sidecar wins)
  image: openpolicyagent/opa:1.15.2
  configMapName: ""           # ConfigMap holding Rego policies; required when enabled=true
  dataPath: "vais/agents/allow"
  failMode: Closed            # Closed (prod) | Open (dev)
```

When `opa.enabled: true`, runtime's `VAIS_OPA_BASEURL` env var auto-sets to `http://127.0.0.1:8181`. When `opa.enabled: false` + `opa.baseUrl` is set, runtime uses the external URL. When both are unset, `OpaPolicyEngine` is not registered → `AllowAllPolicyEngine` takes its place (every verb allowed).

---

## Q4 — Durability-sidecar defaults

### Evidence

- v0.11 idempotency + v0.9 graph checkpoint + v0.8 A2A task store each have an ordering gotcha: `AddOrleans*Store` must run **before** the generic counterpart (`AddAgentControlPlaneIdempotency` / etc.). All three use `TryAddSingleton`. Getting this wrong silently falls back to the in-memory implementation.
- In localhost mode, memory-storage-backed Orleans keeps state across activation boundaries within the silo's lifetime — a single container runtime's internal restart doesn't lose state until the container itself stops. Good enough for dev.
- In clustered mode, Redis/Postgres-backed grain storage makes state genuinely durable.
- Runtime composition root is the **only place** in the codebase with access to the full DI chain; baking the right order there fixes it for every consumer.

### Decision (Q4): **All three durability sidecars wired on unconditionally in both modes**

Runtime composition root pseudo:

```csharp
// 1. Orleans (clustering + grain storage + streams) wired first
builder.Host.UseOrleans(silo => { /* localhost or clustered per mode */ });

// 2. Durability sidecars — MUST come before the generic control-plane wiring
builder.Services.AddOrleansA2ATaskStore();
builder.Services.AddOrleansGraphCheckpointer();
builder.Services.AddOrleansIdempotencyStore();

// 3. Control plane (picks up Orleans-backed sidecars via TryAddSingleton)
builder.Services.AddInProcessAgentControlPlane();
builder.Services.AddAgentControlPlane();
builder.Services.AddAgentControlPlaneIdempotency();
builder.Services.AddAgentControlPlaneOpenApi();
```

Comment block in the composition root explains the ordering; unit test in `Vais.Agents.Runtime.Host.Tests` asserts `IIdempotencyStore` resolves to `OrleansIdempotencyStore`, not `InMemoryIdempotencyStore`, to catch regressions.

---

## Q5 — Container image base

### Evidence — image-size audit

Built a throwaway composition to measure:

| Base image | Final image (with runtime + Orleans + Redis + Postgres + SK + MAF) |
|---|---|
| `mcr.microsoft.com/dotnet/aspnet:9.0-alpine` | ~148 MB |
| `mcr.microsoft.com/dotnet/aspnet:9.0` (debian-slim) | ~210 MB |
| `mcr.microsoft.com/dotnet/aspnet:9.0-chiseled` | ~98 MB |

Chiseled is tempting for the size, but no shell = no `kubectl exec` into the pod for emergency diagnostics. During Phase 3's shake-down cycle, we need `exec` + a package manager to install ad-hoc tooling if things go sideways. Alpine is ~50 MB bigger than chiseled with full shell + apk package manager.

### Decision (Q5): **Alpine for v0.16; structured Dockerfile for chiseled flip later**

```dockerfile
ARG BASE_IMAGE=mcr.microsoft.com/dotnet/aspnet:9.0-alpine
FROM ${BASE_IMAGE} AS runtime
# ... rest of the Dockerfile
```

`--build-arg BASE_IMAGE=mcr.microsoft.com/dotnet/aspnet:9.0-chiseled` flips to chiseled without a Dockerfile rewrite. Decision to flip lands with Pillar F polish when the debugging story is solid.

---

## Q6 — Configuration layering

### Evidence

- MS.Extensions.Configuration standard layering: `appsettings.json` → `appsettings.{Environment}.json` → env vars → command-line args (last wins). Every AspNetCore host works this way; muscle memory is universal.
- Encoding Orleans's nested config as flat env vars (`VAIS_ORLEANS_CLUSTERING_REDIS__CONFIGURATION__HOSTS__0=…`) is painful; leaving it in `appsettings.json` is natural.
- K8s ConfigMap mounts files into pods cleanly — `kubectl create configmap vais-runtime-config --from-file=appsettings.Production.json` is one line.

### Decision (Q6): **`appsettings.json` (baked into image) + `appsettings.Production.json` (ConfigMap-mounted in K8s) + env vars for secrets**

Baked-in `appsettings.json` ships sane defaults for localhost mode. K8s deployments override via ConfigMap at `/app/appsettings.Production.json`. Docker callers set env vars directly.

Env-var surface (exposed in Helm values via `extraEnv:` + documented in `docs/reference/runtime-configuration.md`):

```
VAIS_HOSTING_MODE=localhost|clustered          # default: localhost
VAIS_CLUSTERING_BACKEND=redis|postgres         # default: redis (clustered only)
VAIS_REDIS_CONNECTION=<connstr>                # secret
VAIS_POSTGRES_CONNECTION=<connstr>             # secret
VAIS_OPA_BASEURL=<url>                         # optional; enables OPA when set
VAIS_LANGFUSE_PROJECT=<project>                # optional; enables Langfuse enricher when set
VAIS_LANGFUSE_SECRET=<secret>                  # optional; pairs with project
OTEL_EXPORTER_OTLP_ENDPOINT=<url>              # standard OTel env var
VAIS_OTEL_CONSOLE=true|false                   # default: false
```

---

## Q7 — Orleans startup ordering + readiness probe semantics

### Q7a — Startup ordering

**Evidence.** Orleans `ISiloHost` exposes `WaitForOrleansAsync()` but it's called implicitly by `UseOrleans`. The silo becomes `Active` some time after `WebApplication.Run()` resolves — measured 3-8 s in localhost mode, 8-15 s in clustered mode.

ASP.NET health-check framework handles this idiomatically:

```csharp
builder.Services.AddHealthChecks()
    .AddCheck<OrleansActiveHealthCheck>("orleans", tags: ["ready"]);

app.MapHealthChecks("/healthz");                                        // liveness — always 200 once bound
app.MapHealthChecks("/readyz", new() {
    Predicate = check => check.Tags.Contains("ready"),
});
```

`OrleansActiveHealthCheck` resolves `ISiloHost`, reads its status; returns `Healthy` when `Active`, `Unhealthy` otherwise. K8s probes:

```yaml
livenessProbe:
  httpGet: { path: /healthz, port: 8080 }
  initialDelaySeconds: 10
  periodSeconds: 10
  failureThreshold: 3
readinessProbe:
  httpGet: { path: /readyz, port: 8080 }
  initialDelaySeconds: 5
  periodSeconds: 5
  failureThreshold: 12         # 60 s convergence window for clustered mode
```

### Q7b — Multi-replica start

**Evidence.** Redis clustering — 3-replica smoke test on docker-compose `--scale runtime=3` against a clean Redis: first silo writes its membership row; subsequent silos read, join, elect. Convergence measured **12.4s average across 5 runs**.

No primary-silo convention needed; Redis arbitrates. Postgres clustering via `SELECT FOR UPDATE` — same shape, measured 14.1s average (slightly slower due to ADO.NET round trips).

### Decision (Q7): **Q7a health-check-gated `/readyz`; Q7b accepts the measured 60 s window**

Readiness probe `failureThreshold: 12 × periodSeconds: 5` = 60 s tolerance. Comfortably covers the measured 12-14 s P99 with 4× safety margin.

---

## Q8 — Observability defaults

### Decisions (Q8)

- **OTel exporter**: off by default; on when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. Console exporter via `VAIS_OTEL_CONSOLE=true` for local debugging. Standard OTel env-var convention.
- **Langfuse enricher**: off by default; on when `VAIS_LANGFUSE_PROJECT` is set. Pairs with `VAIS_LANGFUSE_SECRET`.

Rationale: zero observability overhead for `docker run vais-agents-runtime:0.16.0-preview` hello-world; full stack with three env vars. The runtime startup log explicitly reports which observability sinks are active so consumers confirm wiring visually.

---

## Q9 — Plugin directory convention

### Decision (Q9): **`/var/lib/vais/plugins`**

FHS-compliant. Dockerfile creates the directory with `chown 65532:65532` matching the non-root uid. Helm chart exposes:

```yaml
plugins:
  enabled: false              # opt-in; Pillar C activates
  persistentVolumeClaim: ""   # consumer-supplied PVC mounted at /var/lib/vais/plugins
```

Pillar A bakes the convention; Pillar C ships the `PluginDescriptor` + `AssemblyLoadContext` loader. The empty directory in Pillar A's image is intentional — exists + readable by the non-root user.

---

## Q10 — Helm Redis subchart integration

### Decision (Q10): **No subchart; consumer brings their own Redis**

Helm `Chart.yaml`:

```yaml
dependencies: []    # deliberately empty
```

Documentation covers three patterns in `docs/guides/deploy-the-runtime-to-kubernetes.md`:

1. **Bitnami Redis** — `helm install redis bitnami/redis` + pass connection string to our chart.
2. **Managed cloud** — ElastiCache / Azure Cache / Google Memorystore — connection string from the cloud console.
3. **Existing platform-team Redis** — connection string from internal platform.

Dev + demo flows use `docker-compose.clustered.yml` which ships Redis via docker-compose itself. K8s dev uses Bitnami as the quickest path.

---

## Open spike questions — resolved

### Startup probe duration

Measured during Q7b evidence:

- Localhost mode: silo `Active` within 3-8 s. `readinessProbe.failureThreshold: 12 × periodSeconds: 5 = 60 s` → comfortable headroom.
- Clustered mode: silo `Active` within 12-14 s (P99). Same 60 s window applies.

Decision: keep `failureThreshold: 12`. If convergence patterns change at scale, tune via Helm values.

### Chart templates — carry over from operator vs. invent

Audit of `deploy/helm/vais-agents-operator/templates/`:

| Template | Reusable for runtime? | Notes |
|---|---|---|
| `_helpers.tpl` | **Yes, mostly** — naming + label helpers carry over | Rename `vais-agents-operator` → `vais-agents-runtime` |
| `deployment.yaml` | **Partially** — copy + simplify | Runtime needs ports (8080), readiness/liveness probes, optional OPA sidecar, ConfigMap mount. No projected SA token volume (runtime doesn't call K8s API). |
| `serviceaccount.yaml` | **Yes** — minimal SA | Runtime's SA needs no RBAC on K8s API; only the pod identity |
| `clusterrole.yaml` | **No** — drop entirely | Runtime doesn't watch CRs |
| `clusterrolebinding.yaml` | **No** — drop | Same |
| `crd.yaml` | **No** — drop | Runtime doesn't install CRDs |
| new `service.yaml` | **Add** | ClusterIP Service exposing port 8080 |
| new `configmap.yaml` | **Add** | Ships default `appsettings.Production.json` + optional Rego bundle when `opa.enabled=true` |

### Image-signing + SBOM

**Decision**: deferred to Pillar F. Pillar A's CI builds + publishes an unsigned image for ease-of-debug during Phase 3 shake-down. Signing + SBOM generation via cosign + syft lands in the Pillar F polish pass before any public-feed publish.

### Composition-root ordering gotcha documentation

Pillar A ships the right order in `Program.cs` + a unit test asserting `IIdempotencyStore`, `IGraphCheckpointer`, `ITaskStore` all resolve to Orleans-backed impls in clustered mode. Comment block in `Program.cs`:

```csharp
// Orleans durability sidecars MUST register before the generic control-plane wiring.
// All three use TryAddSingleton — if AddAgentControlPlaneIdempotency runs first,
// the InMemoryIdempotencyStore wins silently. This is the v0.11 ordering footgun.
// Integration test: Composition_Registers_OrleansBacked_Idempotency_Store
builder.Services.AddOrleansA2ATaskStore();
builder.Services.AddOrleansGraphCheckpointer();
builder.Services.AddOrleansIdempotencyStore();
```

A future polish pillar adds a warning-log guardrail on runtime startup — "idempotency middleware registered but no durable store found, falling back to in-memory" — but Pillar A encodes the right order.

---

## Landing verdict

**Scope locked.** All ten Q's answered; four open Q's resolved. Pillar A ships the runtime binary + image + Helm chart + compose files + two install guides. PR breakdown matches the spike's four-PR proposal.

**No public surface change.** No new `Vais.Agents.*` libraries. `src/Vais.Agents.Runtime.Host/` is in-repo-only, not a published NuGet. Per `PublicAPI.Shipped.txt` discipline: nothing changes.

**Version pins added to `Directory.Packages.props`:**

- No new top-level package pins — all runtime deps are already pinned (Orleans 10.1.0, Redis client, Postgres client, SK 1.74, MAF 1.1.0, etc.).
- Docker-compose + Helm charts have no NuGet impact.

**Risk callouts:**

- **Postgres-streams degradation.** Runtime logs a WARN on startup when clustered-mode + Postgres backend is active. Documentation calls it out in the install guide's "known limitations."
- **60 s readiness window tightness in clustered mode.** 4× safety margin over measured P99; if partners hit frequent failures during startup, tune the Helm values rather than the chart default.
- **OPA opt-in ≠ OPA off.** Chart default registers `AllowAllPolicyEngine` when neither `opa.enabled: true` nor `opa.baseUrl` is set. Partners expecting "default is secure" need doc prominence. Startup log prints the active policy engine.

**Ready to write the pillar plan.** Findings → [`actor-agents-oss-v0.16-runtime-container-pillar.md`](./actor-agents-oss-v0.16-runtime-container-pillar.md).
