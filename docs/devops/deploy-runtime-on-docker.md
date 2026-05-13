# Deploy the runtime on Docker

You'll build the `vais-agents-runtime` image, start it with Docker Compose, and verify the control-plane is up. End state: `curl http://localhost:8080/healthz` returns `{"status":"Healthy"}` and you can `vais apply` / `vais get` / `vais invoke` against the running runtime.

## Prerequisites

- Docker Engine 24+ or Docker Desktop 4.30+ (Compose v2 required).
- The repo checked out locally — the runtime image is built from source until a public image pipeline ships.

## What you'll end up with

```
┌─ localhost quickstart ─────────────┐   ┌─ clustered evaluation stack ───────┐
│                                    │   │                                    │
│  vais-runtime (one container)      │   │  vais-runtime × 3 replicas         │
│  └── memory clustering + storage   │   │  └── Redis-backed clustering       │
│                                    │   │  └── Redis grain storage           │
│  :8080  /healthz  /openapi/v1.json │   │  :8080 + opa / langfuse / otel    │
│                                    │   │     overlays (optional)           │
│  zero external deps                │   │                                    │
└────────────────────────────────────┘   └────────────────────────────────────┘
```

## 1. Build the runtime image

```bash
cd agentic

docker build \
  -t vais-agents-runtime:local \
  -f src/Vais.Agents.Runtime.Host/Dockerfile \
  .
```

The build uses `mcr.microsoft.com/dotnet/sdk:9.0-alpine` for the build stage and `mcr.microsoft.com/dotnet/aspnet:9.0-alpine` at runtime. The final image runs as uid/gid 65532 (non-root), mounts `/var/lib/vais/plugins` as a volume, and exposes `/healthz`, `/readyz`, and `/openapi/v1.json` on port 8080. Expect ~150 MB.

```bash
docker images | grep vais-agents-runtime
# vais-agents-runtime  local  …  148MB
```

## 2. Localhost mode — 30-second start

Single container, zero external deps.

```bash
cd deploy/compose
docker compose -f docker-compose.localhost.yml up -d
```

The container's HEALTHCHECK converges in ~5 s. Verify:

```bash
curl -s http://localhost:8080/healthz       # → {"status":"Healthy"}
curl -s http://localhost:8080/openapi/v1.json | head
```

`/openapi/v1.json` is the full OpenAPI spec — every control-plane verb with Problem Details responses, idempotency headers, SSE streaming envelopes. Feed it to NSwag / Kiota / openapi-typescript to generate clients.

Teardown:

```bash
docker compose -f docker-compose.localhost.yml down
```

In localhost mode, grain state is memory-only and evaporates on `down`. To keep state across restarts without switching to clustered mode, add Postgres for grain storage only — see **[Add Postgres persistence](add-postgres-persistence.md)**.

## 3. Clustered mode — Redis-backed

For state that survives a restart, the clustered base adds a Redis 7-alpine service and flips the runtime's `VAIS_HOSTING_MODE` to `clustered`.

```bash
docker compose -f docker-compose.clustered.yml up -d
docker compose -f docker-compose.clustered.yml logs -f runtime | grep -E 'silo.*active|runtime starting'
```

Readiness flips green in ~14 s P50 / ~30 s P99 — the probe tolerance is 60 s. The Orleans durability sidecars engage automatically.

Verify state survives restart:

```bash
docker compose -f docker-compose.clustered.yml restart runtime
# Redis dump persists in ./data/redis; silo re-joins within ~20 s.
```

See **[Add Redis persistence](add-redis-persistence.md)** for connecting to an external Redis instead of the bundled one.

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
```

Round-robining from the host needs an ingress (nginx / traefik in the overlay), or drop into Kubernetes via the Helm chart — see **[Deploy on Kubernetes](deploy-runtime-on-kubernetes.md)**.

## 4. Layering overlays

Overlays extend the runtime service on the base file. Stack any combination with additional `-f` flags.

### 4.1 OPA policy gating

```bash
docker compose \
  -f docker-compose.localhost.yml \
  -f docker-compose.opa.yml \
  up --build -d
```

`./policies/` is mounted read-only into the OPA sidecar with `--watch`. The shipped `example.rego` is an allow-all starter; swap in one of the richer samples under [`samples/opa-policies/`](../../samples/opa-policies/) for real gating without restarting anything.

Full walkthrough: [Gate agents with OPA](../guides/gate-agents-with-opa.md).

### 4.2 Langfuse traces

```bash
docker compose \
  -f docker-compose.localhost.yml \
  -f docker-compose.langfuse.yml \
  up --build -d
```

Langfuse UI at `http://localhost:3001`. First run: sign up, create a project, copy the public + secret keys into a local `override.yml` — **never commit those keys**.

See **[Wire Langfuse](wire-langfuse.md)** for production wiring.

### 4.3 OTel → Jaeger

```bash
docker compose \
  -f docker-compose.localhost.yml \
  -f docker-compose.otel.yml \
  up --build -d
```

Jaeger UI at `http://localhost:16686`. The runtime pushes OTLP/gRPC to `jaeger:4317`; traces appear within seconds of the first request.

### 4.4 Full-fat evaluation stack

```bash
docker compose \
  -f docker-compose.clustered.yml \
  -f docker-compose.opa.yml \
  -f docker-compose.langfuse.yml \
  -f docker-compose.otel.yml \
  up --build -d
```

Five services, three integration points, fully wired. Use this shape to demo the runtime to a partner end-to-end.

## 5. Talk to the runtime with the `vais` CLI

```bash
dotnet tool install -g Vais.Agents.Cli

vais config set-context local --server http://localhost:8080
vais config use-context local
vais version
vais get
```

Apply your first manifest and invoke it via **[Your first declarative agent](../agent-developer/your-first-declarative-agent.md)**.

## What you built

- A locally-running `vais-agents-runtime` container with HTTP control plane, OpenAPI surface, and `/healthz` + `/readyz` probes.
- Two operational shapes: localhost (single container, zero external deps) and clustered (Redis-backed, Orleans state, restart-survives).
- Composable overlays for OPA, Langfuse, and OTel — orthogonal `-f` flags that don't require runtime rebuilds.

## Next

- **[Deploy the runtime on Kubernetes](deploy-runtime-on-kubernetes.md)** — same runtime, Helm chart, kind or production clusters.
- **[Add Redis persistence](add-redis-persistence.md)** — connect to an external Redis for production-grade clustering.
- **[Add Postgres persistence](add-postgres-persistence.md)** — durable backing for grain storage without Redis.
- [Reference → Runtime configuration](../reference/runtime-configuration.md) — every env var + `appsettings.json` knob.
