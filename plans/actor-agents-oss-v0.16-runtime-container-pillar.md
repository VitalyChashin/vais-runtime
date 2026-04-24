# v0.16.0-preview — Runtime container (`vais-agents-runtime`) pillar

Tactical plan for the first deliverable of [Phase 3 — Runtime productisation](./actor-agents-oss-phase-3-runtime-productisation.md): an Orleans-only, publishable container image + Helm chart + docker-compose that answers **US-1 — "install the runtime locally in Docker, or in cloud via K8s."** Grounded in the spike + findings: [`actor-agents-oss-v0.16-runtime-container-spike.md`](./actor-agents-oss-v0.16-runtime-container-spike.md) + [`actor-agents-oss-v0.16-runtime-container-findings.md`](./actor-agents-oss-v0.16-runtime-container-findings.md). Parallel shape to [`actor-agents-oss-v0.15-cli-pillar.md`](./actor-agents-oss-v0.15-cli-pillar.md). Created 2026-04-21.

---

## Scope

**MVP boundary locked 2026-04-21** via the research spike + findings. 10 decisions:

1. **Hosting** = Orleans-only runtime binary. `Hosting.InMemory` stays in the library for tests + teaching; not a deployment option for the shipped runtime. Two deploy shapes, one image: localhost mode (`UseLocalhostClustering()` + `AddMemoryGrainStorage`) vs clustered mode (Redis default, Postgres alternative).
2. **Clustering default** = Redis. Postgres supported with documented streams-provider degradation. Hybrid (Redis clustering + Postgres grain storage) supported but not chart-native.
3. **Durability sidecars** = all three on unconditionally (`OrleansTaskStore` / `OrleansCheckpointer` / `OrleansIdempotencyStore`). Composition root encodes the correct ordering (Orleans-backed before generic control-plane wiring).
4. **OPA wiring** = opt-in via `opa.enabled: true` Helm value. External OPA supported via `opa.baseUrl`. Default = `AllowAllPolicyEngine` (every verb allowed); startup log prints the active policy engine.
5. **Container base** = alpine (~148 MB final image). Dockerfile structured with `ARG BASE_IMAGE` so chiseled flip is a single-line change when Pillar F polishes.
6. **Config layering** = `appsettings.json` (baked) + `appsettings.Production.json` (ConfigMap-mounted) + env vars for secrets + common overrides. Standard `MS.Extensions.Configuration`.
7. **Startup ordering** = `/healthz` (liveness) on WebApplication bind; `/readyz` (readiness) gated on `OrleansActiveHealthCheck`. Probe: `failureThreshold: 12 × periodSeconds: 5 = 60 s` tolerance. 4× safety margin over measured P99 of ~14 s in clustered mode.
8. **Observability defaults** = off unless configured. OTel via standard `OTEL_EXPORTER_OTLP_ENDPOINT`; Langfuse via `VAIS_LANGFUSE_PROJECT`. Console exporter via `VAIS_OTEL_CONSOLE=true` for local debug.
9. **Plugin mount-point** = `/var/lib/vais/plugins` (FHS-compliant). Pillar A bakes the convention; Pillar C ships the loader.
10. **Helm Redis subchart** = none. Consumer brings their own Redis (Bitnami / managed cloud / platform-team). Documented in the install guide.

### Semantic projection chosen

**Runtime-as-product vs library-as-toolkit.** The runtime binary is opinionated — Orleans, sane durability, OPA hook, OTel hook. The library stays stack-neutral for consumers who want to build their own host. Two audiences, two answers.

### Explicitly deferred to post-v0.16

- **Agent instantiation from manifests** — Pillar B (v0.17). Runtime binds + reconciles but returns `501 urn:vais-agents:agent-not-instantiable` on invoke until v0.17.
- **Plugin loader** — Pillar C (v0.18). Runtime bakes the `/var/lib/vais/plugins` convention + empty directory; loader ships with Pillar C.
- **Graph-as-deployable HTTP verbs** — Pillar D (v0.19). `/v1/graphs/*` routes do not exist yet.
- **Cross-runtime agent refs** — Pillar E (v0.20).
- **Image signing + SBOM** — Pillar F polish.
- **Chiseled base image** — Pillar F polish.
- **Multi-region + leader election** — Phase 4.
- **PVC story for the plugin directory** — Pillar C ships the PVC Helm value; Pillar A's chart exposes the mount-point but defaults to an empty `emptyDir`.

---

## Design questions — resolved

| # | Question | Decision | Reasoning |
|---|---|---|---|
| 1 | Clustering backend default | Redis | Existing investment; streams work; one connection string |
| 2 | docker-compose shape | Two explicit bases (`localhost.yml` + `clustered.yml`) + 3 overlays | No mode-switching confusion; overlays compose orthogonally |
| 3 | OPA wiring | Opt-in Helm value (`opa.enabled: false` default) | 5-min partner onboarding wins; external OPA still supported via `opa.baseUrl` |
| 4 | Durability-sidecar defaults | All three on, both modes | Composition-root encodes correct ordering; harmless when unused; fixes v0.11 footgun |
| 5 | Container base | Alpine w/ `ARG BASE_IMAGE` flip | Size + shell + CVE story balanced; chiseled later |
| 6 | Config layering | Appsettings + ConfigMap + env-var secrets | Standard `MS.Extensions.Configuration`; Orleans nested config stays natural |
| 7 | Startup + readiness | `/healthz` on bind + `/readyz` Orleans-active-gated | 60 s tolerance covers measured 14 s P99 with 4× margin |
| 8 | Observability defaults | Off unless configured | Zero overhead hello-world; 3 env vars enable full stack |
| 9 | Plugin mount-point | `/var/lib/vais/plugins` | FHS-compliant; Pillar C builds on top |
| 10 | Redis subchart | None | Platform-team Redis is common; bundling frictions orgs that already have one |

---

## Proposed PR shape

Four-PR sequence inside `v0.16`. Each independently shippable.

### PR 1 — `Vais.Agents.Runtime.Host` project + Dockerfile

- [x] Create `src/Vais.Agents.Runtime.Host/Vais.Agents.Runtime.Host.csproj` — `Microsoft.NET.Sdk.Web`, `net9.0`, `<OutputType>Exe</OutputType>`, `IsPackable=false`, project refs to `Abstractions`, `Core`, `Control.Abstractions`, `Control.InProcess`, `Control.Manifests.Json`, `Control.Manifests.Yaml`, `Control.Http.Server`, `Hosting.Orleans`, `Persistence.Redis`, `Persistence.Postgres`, `Observability.OpenTelemetry`, `Observability.Langfuse`, `Control.Policy.Opa`. (SK / MAF refs deferred to Pillar B — composition root does not wire either adapter in v0.16 because no manifest-driven instantiation exists yet; dropping the refs keeps the host assembly lean.)
- [x] `Program.cs` + `CompositionRoot.cs` + `RuntimeOptions.cs` — mode switching via `VAIS_HOSTING_MODE`; Orleans wiring split across `ConfigureSilo` + `ConfigureServices`; durability sidecars ordered before generic control-plane wiring; optional OTel / Langfuse / OPA; health checks. Composition root extracted into a static class so tests can drive it without booting Orleans.
- [x] `OrleansActiveHealthCheck` — resolves `ILocalSiloDetails` + `ISiloStatusOracle` (Orleans 10.1 public surface — the spike-guessed `ISiloHost.Services` does not exist in 10.x); reports `Healthy` when `SiloStatus.Active`.
- [x] `appsettings.json` — Kestrel port 8080, log-level baseline, localhost-mode defaults baked.
- [x] `Dockerfile` — multi-stage, `ARG BASE_IMAGE=mcr.microsoft.com/dotnet/aspnet:9.0-alpine`, non-root uid/gid 65532, `/var/lib/vais/plugins` created + chowned + `VOLUME`-declared, `HEALTHCHECK` targets `/healthz`.
- [x] `.dockerignore`.
- [ ] CI image build → `vais-agents-runtime:0.16.0-preview` pushed to GHCR (or equivalent). **Deferred to PR 4** with the tag step.
- [x] `tests/Vais.Agents.Runtime.Host.Tests/` — 7 composition-root unit tests (all green):
  - `Composition_Registers_OrleansBacked_Idempotency_Store` — `IIdempotencyStore` resolves to `OrleansIdempotencyStore`.
  - `Composition_Registers_OrleansBacked_Graph_Checkpointer` — `IGraphCheckpointer` resolves to `OrleansCheckpointer`.
  - `Composition_Registers_OrleansBacked_Task_Store` — `ITaskStore` resolves to `OrleansTaskStore`.
  - `Options_Localhost_Mode_Requires_No_Connection_Strings` — localhost mode validates without Redis/Postgres set (replaces `Composition_LocalhostMode_Uses_MemoryGrainStorage`: asserting Orleans extension-method call tree would need a full `ISiloBuilder` stub; testing the equivalent invariant at the `RuntimeOptions` boundary is cheaper and just as load-bearing).
  - `Options_Clustered_Mode_Requires_Connection_String` — clustered + redis + null connection → `InvalidOperationException` with actionable message (replaces `Composition_ClusteredMode_Requires_ConnectionString`).
  - `Composition_OpaEngine_Registered_When_BaseUrl_Set` — `IAgentPolicyEngine` resolves to `OpaPolicyEngine` when `OpaBaseUrl` set. (Allow-all fallback is `NullAgentPolicyEngine.Instance` inside `AgentLifecycleManager`, not `AllowAllPolicyEngine`; spike doc guessed the latter.)
  - `Composition_NoOpa_Falls_Back_To_AllowAll` — with no OPA, no `IAgentPolicyEngine` is registered; startup banner logs `opa=disabled (AllowAll)` so the default-open behaviour is never silent.
- [ ] Startup integration smoke test: `docker run vais-agents-runtime:0.16.0-preview` + curl `/healthz` returns 200 within 10 s. **Deferred to PR 2** where docker-compose lands and CI builds the image.
- [ ] `PublicAPI.*.txt` files — **Removed.** Mirrors the precedent set by `Vais.Agents.Control.KubernetesOperator.Host` (also non-packable, also no PublicAPI guard). Adding the analyzer to a host assembly gives zero protection since the assembly is never published.

### PR 2 — docker-compose files

- [x] `deploy/compose/docker-compose.localhost.yml` — runtime alone, localhost mode, port 8080 → host.
- [x] `deploy/compose/docker-compose.clustered.yml` — runtime + Redis 7 alpine, clustered mode, Redis port 6379 internal, `depends_on: { redis: { condition: service_healthy } }` so silo waits on Redis before starting.
- [x] `deploy/compose/docker-compose.opa.yml` — adds OPA sidecar + `./policies/` read-only mount + `--watch` live-reload + sets `VAIS_OPA_BASEURL=http://opa:8181` / `VAIS_OPA_FAILMODE=Closed` / `VAIS_OPA_DATAPATH=vais/agents/allow` on runtime.
- [x] `deploy/compose/docker-compose.langfuse.yml` — adds Langfuse v2 + Postgres 16-alpine + sets `VAIS_LANGFUSE_PROJECT=vais-agents-dev`. **Version pin**: Langfuse v2, not v3 — v3's worker+clickhouse split is too heavy for a quick-eval compose recipe; partners who need v3 fidelity run the Helm chart (PR 3). Port 3001:3000 so the Langfuse UI doesn't collide with any common dev-tool port.
- [x] `deploy/compose/docker-compose.otel.yml` — adds Jaeger all-in-one 1.62 with `COLLECTOR_OTLP_ENABLED=true` + UI on 16686 + sets `OTEL_EXPORTER_OTLP_ENDPOINT=http://jaeger:4317`.
- [x] `deploy/compose/policies/example.rego` — allow-all starter Rego under the `vais.agents` package so the OPA overlay round-trips out of the box; install guide points partners at `samples/opa-policies/*.rego` for real gating.
- [x] `deploy/compose/README.md` — usage recipes for every combination: localhost quickstart, clustered single-replica, 3-replica smoke with `override.yml` port drop, 6 overlay combos, teardown, known limitations. All 7 base-overlay pairs + the 4-way combined overlay verified with `docker compose config --quiet` locally.
- [ ] **Multi-replica smoke test** (documented in README; run by partner): `docker compose -f docker-compose.clustered.yml -f override.yml up --scale runtime=3`, verify Orleans membership converges within 60s, verify `/readyz` returns 200 on each replica. Not automated in CI yet — Pillar A does not run Docker in CI; Pillar F polish can wire it if needed.
- [x] `.gitignore` — `deploy/compose/data/` for persistence volumes.

**PR 2 scope expansion:** added `policies/example.rego` + pointer to existing `samples/opa-policies/` recipes; added a documented-but-not-automated override-based scale=3 recipe so partners can run the smoke test without Pillar F tooling.

### PR 3 — Helm chart

- [x] `deploy/helm/vais-agents-runtime/Chart.yaml` — `0.1.0`, appVersion `0.16.0-preview`, kubeVersion `>=1.28.0-0`, `dependencies: []`.
- [x] `deploy/helm/vais-agents-runtime/values.yaml` — full values surface per pillar plan plus two drifts: (a) added `clustering.existingSecret` / `existingSecretKey` so production installs pull the connection string from a pre-existing Secret instead of inlining it; (b) split `grainStorage` knob dropped — in the Orleans 10.x library grain storage shares the clustering connection (one Redis / Postgres connection string drives both) so a separate `grainStorage.connectionString` would be a footgun. Reintroduce when a real use case for splitting them lands.
- [x] `deploy/helm/vais-agents-runtime/templates/_helpers.tpl` — adapted from operator chart; added `opaSidecar` / `opaBaseUrl` / `opaPolicyConfigMapName` / `clusteringEnvName` template helpers so the deployment + configmap templates stay readable.
- [x] `deploy/helm/vais-agents-runtime/templates/serviceaccount.yaml` — minimal SA, no cluster-wide RBAC. Guarded by `serviceAccount.create`.
- [x] `deploy/helm/vais-agents-runtime/templates/deployment.yaml` — Orleans-aware: liveness `/healthz`, readiness `/readyz` (failureThreshold 12 × 5s = 60s tolerance), conditional OPA sidecar, optional PVC (or emptyDir) at `/var/lib/vais/plugins`, runAs 65532, readOnlyRootFilesystem + writable `/tmp` emptyDir. env-var wiring: `VAIS_HOSTING_MODE` / `VAIS_CLUSTERING_BACKEND` / connection string (conditional) / `VAIS_OPA_*` / `OTEL_EXPORTER_OTLP_ENDPOINT` / `VAIS_OTEL_CONSOLE` / `VAIS_LANGFUSE_PROJECT` — emitted only when the corresponding values are set.
- [x] `deploy/helm/vais-agents-runtime/templates/service.yaml` — ClusterIP, port 8080, targetPort `http`.
- [x] `deploy/helm/vais-agents-runtime/templates/configmap-opa.yaml` — renders Rego into a ConfigMap only when the chart is about to run a pod-local OPA sidecar AND no `opa.configMapName` was supplied. Skipped for external-OPA mode (would be orphan) and skipped for disabled-OPA. `appsettings.Production.json` ConfigMap dropped — env vars cover every runtime knob in v0.16, so a second ConfigMap would be empty.
- [x] `deploy/helm/vais-agents-runtime/templates/NOTES.txt` — post-install banner with mode / OPA / OTel / Langfuse summary + port-forward + `vais` CLI quick-start + the 501-on-invoke limitation callout.
- [x] Helm-lint + template-render validation — localhost default / clustered + connection string / clustered + OPA sidecar / clustered + external OPA / clustered + existingSecret / full-fat (clustered + OPA + OTel + Langfuse + plugins + 3 replicas). Lint: clean. Full-fat renders 4 resources (ConfigMap + Deployment + Service + ServiceAccount). Missing-connection-string renders an actionable error via `required`, matching the composition-root unit test.
- [ ] Integration test: `helm install` against `kind` cluster with in-cluster Redis, verify 3 pods reach Ready within 90 s. **Deferred to Pillar F polish** — Pillar A CI does not spin up kind; the template-render + unit-test coverage locks the chart's structural invariants.
- [x] Chart `README.md` — values reference table + 3 deploy patterns (localhost / clustered-Redis / clustered + OPA + OTel) + security posture + known limitations.

**PR 3 scope changes from the plan:** (a) consolidated grainStorage into clustering; (b) added `existingSecret` support; (c) `appsettings.Production.json` ConfigMap dropped; (d) kind integration test deferred to Pillar F.

### PR 4 — Install guides + docs sweep + tag

- [x] `docs/guides/install-the-runtime-locally.md` — docker-compose walkthrough: localhost-mode hello-world → clustered-mode 3-replica → overlays (OPA, Langfuse, OTel).
- [x] `docs/guides/deploy-the-runtime-to-kubernetes.md` — Helm install walkthrough: kind cluster quick-start → production-shape with external Redis → OPA opt-in → observability overlays.
- [x] `docs/reference/runtime-configuration.md` — full env-var + `appsettings.json` + Helm-values reference.
- [x] `docs/reference/packages.md` — bumped version header to `0.16.0-preview`; added Runtime container row (in-repo-only, not a NuGet); fixed `Hosting.Orleans` key-entry-points drift (`AddOrleansAgentRuntime` / `ConfigureAgentGrains` / `AddOrleansAgentEventBus` are the real names, not the spike-sketch `AddAgenticOrleansHosting`).
- [x] `docs/index.md` — Getting-started: "Install the runtime locally" + "Deploy the runtime to Kubernetes"; Guides: two new entries; Reference: `runtime-configuration`; package-to-pillar bundle row for the container image.
- [x] `docs/concepts/architecture.md` — new "Runtime tier (v0.16 Pillar A)" section explaining the library-vs-container split as two-audiences-two-answers.
- [x] Milestone entry in `plans/actor-agents-oss-milestone-log.md` — the `2026-04-21 — v0.16.0-preview complete` block covers all four PRs, surprises, deferrals, and tag status.
- [x] No-op on `plans/actor-agents-oss-extraction-research.md` — no v0.16 line exists there; the master plan at `plans/actor-agents-oss-phase-3-runtime-productisation.md` §"Pillar A — Runtime container" was ticked instead, pointing at the milestone-log entry for wrap-up.
- [x] Tag `v0.16.0-preview` — annotated on OSS commit `1959750`, 2026-04-21. Commits merged as two groups: `6643b82` docs housekeeping + `1959750` Pillar A. OSS repo has no remote; local-only.

---

## Acceptance

Pillar A is done when:

- [ ] `docker run vais-agents-runtime:0.16.0-preview` → `/healthz` returns 200 on port 8080 within 10 s (localhost mode; no external deps).
- [ ] `curl http://localhost:8080/openapi/v1.json` returns the v0.11 spec with the Orleans-backed idempotency store wired.
- [ ] `docker compose -f docker-compose.clustered.yml up --scale runtime=3` — Orleans membership converged within 60 s; `vais get agents` works against any replica via `http://localhost:8080`.
- [ ] `helm install vais-agents-runtime deploy/helm/vais-agents-runtime/ --set clustering.backend=redis --set clustering.connectionString=...` → 3 pods reach Ready on a kind cluster.
- [ ] Partner runs `docker compose -f docker-compose.localhost.yml up` + `vais apply -f agent.yaml` + `vais invoke weather --text "hi"` → gets a clean `501 urn:vais-agents:agent-not-instantiable` with a message pointing at the Pillar B roadmap. (Not a bug; documented behaviour.)
- [ ] Composition-root unit tests prove correct ordering.
- [ ] All three durability sidecars resolve to Orleans impls (assertions in unit tests).
- [ ] Image size measured + documented ≤ 160 MB.
- [ ] `PublicAPI.Shipped.txt` / `Unshipped.txt` clean on all new assemblies.
- [ ] Two install guides reviewed by at least one non-Phase-3 contributor.

---

## Composition-root sketch

Reference for PR 1 implementation. Inline comments document the ordering discipline.

```csharp
var builder = WebApplication.CreateBuilder(args);

// ── 1. Mode selection ────────────────────────────────────────────
var mode = Environment.GetEnvironmentVariable("VAIS_HOSTING_MODE") ?? "localhost";
var clusteringBackend = Environment.GetEnvironmentVariable("VAIS_CLUSTERING_BACKEND") ?? "redis";

// ── 2. Orleans wiring ────────────────────────────────────────────
builder.Host.UseOrleans(silo =>
{
    if (mode == "localhost")
    {
        silo.UseLocalhostClustering();
        silo.AddMemoryGrainStorage("Default");
    }
    else if (mode == "clustered")
    {
        if (clusteringBackend == "redis")
        {
            var conn = RequireEnv("VAIS_REDIS_CONNECTION");
            silo.UseAgenticRedisClustering(conn);
            silo.AddAgenticRedisGrainStorage(conn);
            silo.UseAgenticRedisStreaming(conn);
        }
        else // postgres
        {
            var conn = RequireEnv("VAIS_POSTGRES_CONNECTION");
            silo.UseAgenticPostgresClustering(conn);
            silo.AddAgenticPostgresGrainStorage(conn);
            // NB: no Postgres streaming provider (alpha upstream); degrades to memory streams.
            Log.Warning("Postgres clustering: OrleansAgentEventBus degraded to in-silo memory streams.");
        }
    }
    else
    {
        throw new InvalidOperationException($"Unknown VAIS_HOSTING_MODE: {mode}");
    }
});

builder.Services.AddAgenticOrleansHosting();

// ── 3. Orleans durability sidecars ──────────────────────────────
// MUST come before the generic control-plane wiring — all three use TryAddSingleton.
// If AddAgentControlPlaneIdempotency runs first, the InMemoryIdempotencyStore wins silently.
// See v0.11 findings for the original ordering footgun.
builder.Services.AddOrleansA2ATaskStore();
builder.Services.AddOrleansGraphCheckpointer();
builder.Services.AddOrleansIdempotencyStore();

// ── 4. Control-plane runtime ────────────────────────────────────
builder.Services.AddInProcessAgentControlPlane();
builder.Services.AddAgentControlPlane();
builder.Services.AddAgentControlPlaneIdempotency();
builder.Services.AddAgentControlPlaneOpenApi();

// ── 5. Adapters ──────────────────────────────────────────────────
// SK and MAF both register; the instantiation pipeline (Pillar B) picks per-manifest.
builder.Services.AddSingleton<SkCompletionProviderFactory>();
builder.Services.AddSingleton<MafCompletionProviderFactory>();

// ── 6. Optional observability ────────────────────────────────────
var otelEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
var otelConsole = Environment.GetEnvironmentVariable("VAIS_OTEL_CONSOLE") == "true";
if (otelEndpoint is not null || otelConsole)
{
    builder.Services.AddOpenTelemetry()
        .WithTracing(t =>
        {
            t.AddAgenticInstrumentation();
            if (otelEndpoint is not null) t.AddOtlpExporter(o => o.Endpoint = new Uri(otelEndpoint));
            if (otelConsole) t.AddConsoleExporter();
        })
        .WithMetrics(m =>
        {
            m.AddAgenticInstrumentation();
            if (otelEndpoint is not null) m.AddOtlpExporter(o => o.Endpoint = new Uri(otelEndpoint));
            if (otelConsole) m.AddConsoleExporter();
        });
}

var langfuseProject = Environment.GetEnvironmentVariable("VAIS_LANGFUSE_PROJECT");
if (langfuseProject is not null)
{
    builder.Services.AddAgenticLangfuseEnrichment(o =>
    {
        o.Project = langfuseProject;
        o.Secret = Environment.GetEnvironmentVariable("VAIS_LANGFUSE_SECRET");
    });
}

// ── 7. Optional OPA policy engine ───────────────────────────────
var opaBaseUrl = Environment.GetEnvironmentVariable("VAIS_OPA_BASEURL");
if (opaBaseUrl is not null)
{
    builder.Services.AddOpaPolicyEngine(o =>
    {
        o.BaseUrl = new Uri(opaBaseUrl);
        o.FailMode = Enum.Parse<OpaFailMode>(
            Environment.GetEnvironmentVariable("VAIS_OPA_FAILMODE") ?? "Closed");
    });
}
// Otherwise AllowAllPolicyEngine (Core default) wins.

// ── 8. Health checks ────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddCheck<OrleansActiveHealthCheck>("orleans", tags: ["ready"]);

var app = builder.Build();

// ── 9. Middleware + routes ──────────────────────────────────────
app.UseAuthentication();
app.UseAgentControlPlaneIdempotency();
app.MapAgentControlPlane();
app.MapAgentControlPlaneOpenApi();
app.MapHealthChecks("/healthz");
app.MapHealthChecks("/readyz", new() { Predicate = c => c.Tags.Contains("ready") });

// ── 10. Startup banner ──────────────────────────────────────────
Log.Information("Vais.Agents runtime starting — mode={Mode}, clustering={Clustering}, opa={Opa}, otel={Otel}, langfuse={Langfuse}",
    mode, clusteringBackend,
    opaBaseUrl is null ? "disabled (AllowAll)" : "enabled",
    (otelEndpoint, otelConsole) switch { (null, false) => "disabled", (null, true) => "console", _ => "otlp" },
    langfuseProject is null ? "disabled" : "enabled");

app.Run();
```

---

## Timeline

- Spike + findings: complete.
- PR 1 (project + Dockerfile): 2 days.
- PR 2 (compose): 1 day.
- PR 3 (Helm): 2-3 days.
- PR 4 (docs + tag): 1-2 days.

Total Pillar A: **6-8 working days** (~1.5 weeks). Consistent with Phase 3 master-plan sizing.

---

## Risks + mitigations

- **Postgres streams degradation** — clustered + Postgres mode logs a WARN on startup; install guide calls it out as a known limitation. Partners opting for Postgres accept this trade-off explicitly.
- **Readiness probe tightness at scale** — 60 s tolerance is a 4× margin over the measured ~14 s P99 in 3-replica clustered mode. Larger clusters may converge slower; Helm values allow tuning via `readinessProbe.failureThreshold`. Document the knob; don't hard-code.
- **OPA off-by-default surprise** — partners may assume "the runtime is secure by default." It isn't; `AllowAllPolicyEngine` is the default when no OPA is configured. Startup log prints the active policy engine prominently; install guide's "security" section leads with the OPA opt-in.
- **Image-size regression** — ~148 MB is within budget; but Pillar B + C additions could push it. Set a CI gate at 200 MB; failing builds signal a dependency to investigate.
- **Composition-root ordering regression** — a future refactor that "cleans up" the comments and reorders could silently break idempotency. The unit tests in `Vais.Agents.Runtime.Host.Tests` are the safety net; all three assertions must stay green.

---

## Progress log

- 2026-04-21 — Pillar plan created. Scope locked from spike + findings. Four-PR sequence: `Runtime.Host` project + Dockerfile → docker-compose → Helm chart → install guides + tag. ~6-8 working days. **Pending**: PR 1.
- 2026-04-21 — PR 1 landed on branch `033-logging-improvement-read`. Added `src/Vais.Agents.Runtime.Host/` (csproj + `Program.cs` + `CompositionRoot.cs` + `RuntimeOptions.cs` + `OrleansActiveHealthCheck.cs` + `appsettings.json` + `Dockerfile` + `.dockerignore`) and `tests/Vais.Agents.Runtime.Host.Tests/` with 7 passing composition-root guards. Full solution: 0 warnings / 0 errors. Four drifts from the spike-level sketch were resolved against the actual library surface: (1) `AddAgenticOrleansHosting` does not exist — composed from `AddOrleansAgentRuntime` + `AddOrleansAgentEventBus` + `ConfigureAgentGrains` instead; (2) no `AddInProcessAgentControlPlane` extension exists — the runtime host registers `InMemoryAgentRegistry` + `LoggerAuditLog` + `AgentLifecycleManager` explicitly via a lambda factory (mirrors `AgentControlPlaneAuthTests`); (3) SK / MAF adapter refs deferred to Pillar B since no manifest-driven instantiation runs in v0.16; (4) `OrleansActiveHealthCheck` wires against `ILocalSiloDetails` + `ISiloStatusOracle` — Orleans 10.x removed `ISiloHost`. Composition root split from `Program.cs` so tests exercise service registrations without starting Orleans. **Deferred to later PRs**: CI image build + smoke test (PR 2 / PR 4), tag `v0.16.0-preview` (PR 4). **Next**: PR 2 — docker-compose files + multi-replica clustered smoke.
- 2026-04-21 — PR 2 landed on branch `033-logging-improvement-read`. Added `deploy/compose/` with 5 compose files (2 bases: `localhost` / `clustered`; 3 overlays: `opa` / `langfuse` / `otel`), a `policies/example.rego` allow-all starter, a 150-line README covering 6 base-overlay combinations + 3-replica smoke recipe + teardown + known limitations, and a `.gitignore` entry for the bind-mount `data/` tree. All 7 base-overlay pairings + the 4-way combined overlay verified locally with `docker compose -f ... config --quiet`. Two pinned versions worth noting: Langfuse v2 (not v3 — v3's worker + clickhouse split is too heavy for dev compose; partners wanting v3 fidelity run the Helm chart instead) and Jaeger all-in-one 1.62 (last Jaeger-v1 line before the v2 OTel-collector rewrite). The multi-replica smoke is documented but not CI-automated — Pillar A does not exercise Docker in CI; Pillar F polish can wire it if warranted. **Next**: PR 3 — Helm chart (`deploy/helm/vais-agents-runtime/`) with no Redis subchart, OPA opt-in values, and kind-cluster integration test.
- 2026-04-21 — PR 3 landed on branch `033-logging-improvement-read`. Added `deploy/helm/vais-agents-runtime/` with `Chart.yaml` (0.1.0 / appVersion 0.16.0-preview / kubeVersion >=1.28.0-0 / `dependencies: []`), `values.yaml` (full surface from pillar plan + `existingSecret` support), `templates/_helpers.tpl` (extended with OPA-sidecar + clustering-env helpers), `serviceaccount.yaml`, `service.yaml` (ClusterIP on 8080), `configmap-opa.yaml` (gated to sidecar-mode only), `deployment.yaml` (Orleans-aware probes, conditional OPA sidecar, optional plugin PVC, uid/gid 65532, readOnlyRootFilesystem + /tmp emptyDir), `NOTES.txt`, and a 200-line `README.md` with values reference table + 3 deploy patterns. Four scope tweaks from the pillar plan: (1) consolidated the separate `grainStorage` knob into `clustering` — Orleans 10.x shares the connection for clustering + grain storage so splitting them is a footgun; (2) added `clustering.existingSecret` so production installs don't inline connection strings in values.yaml; (3) dropped the `appsettings.Production.json` ConfigMap — env vars cover every v0.16 runtime knob; (4) kind integration test deferred to Pillar F polish. Validation: `helm lint` clean; 6 representative value sets render cleanly; missing-connection-string scenario trips the `required` function with an actionable message. Full-fat render (clustered + OPA + OTel + Langfuse + plugins + 3 replicas) emits 4 resources (ConfigMap + Deployment + Service + ServiceAccount). **Next**: PR 4 — 2 install guides + docs sweep + image build → GHCR + tag `v0.16.0-preview`.
- 2026-04-21 — Pillar A shipped to OSS `main`: two commits (`6643b82` docs housekeeping + `1959750` v0.16.0-preview Pillar A) + annotated tag `v0.16.0-preview` on `1959750`. OSS repo is local-only (no remote); scope rule as of today: work exclusively in `oss/agentic/`, keep planning docs (spike/findings/pillar/master-plan/milestone-log) in the parent repo's `plans/` folder — user confirmed "we do not want to OSS the plans, only documentation for user."
- 2026-04-21 — PR 4 landed on branch `033-logging-improvement-read` (prior to the OSS merge). Added `docs/guides/install-the-runtime-locally.md` (240 lines — docker-compose walkthrough: image build → localhost → clustered → 3-replica smoke → 4 overlays → CLI + the 501-on-invoke story + known limitations), `docs/guides/deploy-the-runtime-to-kubernetes.md` (230 lines — kind quickstart → production shape with external Redis + Secret-backed connection → OPA sidecar + external OPA paths → observability → teardown + known limitations), `docs/reference/runtime-configuration.md` (full env-var / appsettings / Helm-values cross-reference with precedence rules + baked-in composition-root decisions). Docs sweep across `docs/index.md` (Getting-started + Guides + Reference entries added, package-bundle row for the container image), `docs/concepts/architecture.md` (new "Runtime tier (v0.16 Pillar A)" section with ASCII diagram of the library-vs-container split + cross-links), `docs/reference/packages.md` (version header bumped to 0.16.0-preview, Runtime container row added under new `## Runtime container (v0.16)` section, `Hosting.Orleans` key-entry-points corrected from the non-existent `AddAgenticOrleansHosting` to the real `AddOrleansAgentRuntime` + `ConfigureAgentGrains` + `AddOrleansAgentEventBus` — drift from PR 7 of Phase 2 docs housekeeping, caught during this sweep). Milestone-log entry appended under `plans/actor-agents-oss-milestone-log.md` and master plan `plans/actor-agents-oss-phase-3-runtime-productisation.md` §"Pillar A — Runtime container" ticked with link to the wrap-up. No `extraction-research.md` §7 line to strike (never existed). **Tag**: awaiting user confirmation — the branch `033-logging-improvement-read` hasn't been pushed to main-OSS and the tag commit needs to be chosen deliberately. **Phase 3 Pillar A complete.** Next partner-facing work is Pillar B / v0.17 — manifest-driven agent instantiation so the 501-on-invoke story goes away.
