# Guide: install the runtime locally

End-to-end walkthrough from a clean laptop to a running `vais-agents-runtime` container serving `/healthz` and the v0.11 OpenAPI spec. Uses the docker-compose recipes that ship under [`deploy/compose/`](../../deploy/compose/README.md) — two bases (`localhost` / `clustered`) and three overlays (`opa` / `langfuse` / `otel`) that compose orthogonally.

Prereqs: Docker Engine 24+ or Docker Desktop 4.30+ (Compose v2 required), the OSS repo checked out locally.

## What we'll end up with

```
┌─ localhost quickstart ─────────────┐      ┌─ clustered evaluation stack ───────┐
│                                    │      │                                    │
│  vais-runtime (one container)      │      │  vais-runtime × 3 replicas         │
│  └── memory clustering + storage   │      │    └── Orleans silo-to-silo membership
│  └── in-silo memory streams        │      │       via Redis clustering         │
│                                    │      │    └── Redis grain storage         │
│  :8080  /healthz  /openapi/v1.json │      │                                    │
│                                    │      │  opa sidecar (optional)            │
│  zero external deps                │      │  jaeger / langfuse (optional)      │
│                                    │      │                                    │
└────────────────────────────────────┘      └────────────────────────────────────┘
```

## 1. Build the runtime image

The runtime image is built locally from the `Vais.Agents.Runtime.Host` project's Dockerfile. No public image pipeline ships in v0.16 — partners build + tag.

```bash
cd oss/agentic

docker build \
  -t vais-agents-runtime:0.16.0-preview \
  -f src/Vais.Agents.Runtime.Host/Dockerfile \
  .
```

The build uses `mcr.microsoft.com/dotnet/sdk:9.0-alpine` for the build stage and `mcr.microsoft.com/dotnet/aspnet:9.0-alpine` for runtime. The final image runs as uid/gid 65532 (non-root), mounts `/var/lib/vais/plugins` as a volume (Pillar C ships the loader; the convention is baked now), and exposes `/healthz` + `/readyz` + `/openapi/v1.json` on port 8080. Expect ~150 MB.

```bash
docker images | grep vais-agents-runtime
# vais-agents-runtime  0.16.0-preview  …  148MB
```

## 2. Localhost mode — 30-second quickstart

Single container, zero external deps. Exactly what the composition-root unit tests exercise.

```bash
cd deploy/compose

docker compose -f docker-compose.localhost.yml up -d
```

The container's HEALTHCHECK converges in ~5 s. Verify:

```bash
curl -s http://localhost:8080/healthz       # → {"status":"Healthy"}
curl -s http://localhost:8080/openapi/v1.json | head
```

`/openapi/v1.json` is the full v0.11 spec — every control-plane verb with Problem Details responses, idempotency-key headers, SSE streaming envelopes, the works. Feed it to NSwag / Kiota / openapi-typescript to generate clients (see [consume-the-openapi-spec.md](./consume-the-openapi-spec.md)).

Teardown:

```bash
docker compose -f docker-compose.localhost.yml down
```

Grain state evaporates on `down` — localhost mode is for demos and smoke tests, not durability.

## 3. Clustered mode — Redis-backed

For state that survives a restart, the clustered base adds a Redis 7-alpine service and flips the runtime's `VAIS_HOSTING_MODE` to `clustered`.

```bash
docker compose -f docker-compose.clustered.yml up -d
docker compose -f docker-compose.clustered.yml logs -f runtime | grep -E 'silo.*active|Vais\.Agents runtime starting'
```

Readiness flips green in ~14 s P50 / ~30 s P99 — the probe tolerance is 60 s. The three durability sidecars engage automatically (`OrleansTaskStore` / `OrleansCheckpointer` / `OrleansIdempotencyStore`); the composition-root unit tests guard the registration order so `TryAddSingleton` picks the Orleans impls over the in-memory defaults.

Verify state survives restart:

```bash
docker compose -f docker-compose.clustered.yml restart runtime
# Redis dump persists in ./data/redis; silo re-joins within ~20 s.
```

### 3.1 Multi-replica smoke

The single-replica base publishes `8080:8080` to the host. Under `--scale runtime=3` that port binding conflicts — drop the host port via an `override.yml`:

```bash
cat > docker-compose.override.yml <<'EOF'
services:
  runtime:
    ports: []
EOF

docker compose \
  -f docker-compose.clustered.yml \
  -f docker-compose.override.yml \
  up --scale runtime=3 -d

# All 3 replicas reach SiloStatus.Active within ~60 s.
for i in 1 2 3; do
  docker compose -f docker-compose.clustered.yml -f docker-compose.override.yml \
    exec -T --index=$i runtime wget -qO- http://localhost:8080/readyz
done
# → {"status":"Healthy"} × 3
```

Round-robining from the host needs an ingress — use nginx / traefik in the overlay, or drop into Kubernetes via the Helm chart ([deploy-the-runtime-to-kubernetes.md](./deploy-the-runtime-to-kubernetes.md)).

## 4. Layering overlays

Overlays extend the runtime service on the base file. Stack any combination with additional `-f` flags:

### 4.1 OPA policy gating

```bash
docker compose \
  -f docker-compose.localhost.yml \
  -f docker-compose.opa.yml \
  up --build -d
```

`./policies/` is mounted read-only into the OPA sidecar with `--watch`. The shipped `example.rego` is an allow-all starter; swap in one of the richer samples under [`samples/opa-policies/`](../../samples/opa-policies/) for real gating without restarting anything. Verify OPA is in the path:

```bash
curl -s http://$(docker compose \
    -f docker-compose.localhost.yml -f docker-compose.opa.yml \
    port opa 8181)/v1/data/vais/agents/allow
# → {"result":{"allowed":true}}
```

Full walkthrough: [gate-agents-with-opa.md](./gate-agents-with-opa.md).

### 4.2 Langfuse traces

```bash
docker compose \
  -f docker-compose.localhost.yml \
  -f docker-compose.langfuse.yml \
  up --build -d
```

Langfuse UI at `http://localhost:3001`. First run, sign up, create a project, copy the public + secret keys into a local `override.yml` — **never commit those keys**. v0.16 wires the project label only; Pillar B expands the enrichment pipeline for full trace ingestion.

### 4.3 OTel → Jaeger

```bash
docker compose \
  -f docker-compose.localhost.yml \
  -f docker-compose.otel.yml \
  up --build -d
```

Jaeger UI at `http://localhost:16686`. The runtime pushes OTLP/gRPC to `jaeger:4317`; traces appear within seconds of the first request. For production, swap the `OTEL_EXPORTER_OTLP_ENDPOINT` env var at your collector via an `override.yml`.

### 4.4 Full-fat evaluation stack

```bash
docker compose \
  -f docker-compose.clustered.yml \
  -f docker-compose.opa.yml \
  -f docker-compose.langfuse.yml \
  -f docker-compose.otel.yml \
  up --build -d
```

Five services, three integration points, fully wired. Use this shape to demo the v0.16 surface to a partner end to end.

## 5. Talk to the runtime with the `vais` CLI

The [CLI](../getting-started/install-the-cli.md) is the idiomatic way to exercise the control-plane verbs against any deployment — docker-compose, Kubernetes, or co-hosted dev.

```bash
dotnet tool install -g Vais.Agents.Cli

vais config set base-url http://localhost:8080
vais version
vais init weather -o weather.yaml
vais apply -f weather.yaml
vais get agents
```

### The 501 you'll see on invoke

```bash
vais invoke weather --text "hi"
# exit 1
# Problem Details:
#   type:   urn:vais-agents:agent-not-instantiable
#   title:  Agent handler not instantiable
#   status: 501
#   detail: Manifest-driven agent instantiation ships with Pillar B (v0.17).
```

This is **documented behaviour**, not a bug. The v0.16 runtime boots Orleans + the durability sidecars + the full HTTP control plane; it does not yet materialize an `IAiAgent` from `AgentManifest.Model + SystemPrompt`. Create / Get / Delete verbs + OpenAPI + idempotency all work today. See the [Pillar B spike](../../../plans/actor-agents-oss-phase-3-runtime-productisation.md) for the Pillar B roadmap.

## Known limitations (v0.16-preview)

- **Invoke returns 501** on every agent until Pillar B ships.
- **Postgres clustering degrades to in-silo memory streams.** The Orleans 10.x ecosystem lacks a production Postgres stream provider; Redis is the default for a reason. Logged as WARN on startup.
- **Scale>1 needs an override.** Single-replica base publishes 8080; drop the host port as shown above when scaling past one.
- **The default policy engine is allow-all.** `opa=disabled (AllowAll)` is logged at startup so the behaviour is never silent, but a dev compose without the OPA overlay applies no gating at all.

## Next

- [deploy-the-runtime-to-kubernetes.md](./deploy-the-runtime-to-kubernetes.md) — same runtime, Helm chart, kind / production clusters.
- [../reference/runtime-configuration.md](../reference/runtime-configuration.md) — full env-var + `appsettings.json` + Helm-values reference.
- [../concepts/architecture.md](../concepts/architecture.md) — where the runtime tier fits in the library's layering.
