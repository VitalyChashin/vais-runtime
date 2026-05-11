# Vais.Agents runtime — docker-compose recipes

Five compose files that stand up the `vais-agents-runtime` container (v0.16
Pillar A) in every supported shape. Two **bases** (`localhost` / `clustered`)
pick the hosting mode; three **overlays** (`opa` / `langfuse` / `otel`) layer
optional integrations on top. Compose them with `-f` flags.

> **Scope:** dev / demo / partner evaluation. Production Kubernetes installs
> ship with the Helm chart in `../helm/vais-agents-runtime/` (PR 3 of
> Pillar A).

## Prerequisites

- Docker Engine 24+ / Docker Desktop 4.30+ (Compose v2 required — the files
  use the `depends_on: { condition: service_healthy }` shape).
- `wget` is baked into the aspnet-alpine base; no extra host tooling needed.
- The first `docker compose up --build` builds the runtime image locally as
  `vais-agents-runtime:0.16.0-preview` from
  [`../../src/Vais.Agents.Runtime.Host/Dockerfile`](../../src/Vais.Agents.Runtime.Host/Dockerfile).

Run every command in this file from `oss/agentic/deploy/compose/` unless a
`cd` is noted.

## Files in this directory

| File | Role | Services |
|---|---|---|
| `docker-compose.localhost.yml` | **Base** — memory clustering, memory grain storage, memory streams. No external deps. | `runtime` |
| `docker-compose.clustered.yml` | **Base** — Redis-backed clustering + grain storage + streams. | `runtime`, `redis` |
| `docker-compose.opa.yml` | Overlay — OPA sidecar, `./policies` hot-mounted. | `opa` + runtime env |
| `docker-compose.langfuse.yml` | Overlay — Langfuse v2 UI on `http://localhost:3001` + its Postgres. | `langfuse-server`, `langfuse-db` + runtime env |
| `docker-compose.otel.yml` | Overlay — Jaeger all-in-one UI on `http://localhost:16686`, OTLP/gRPC on `jaeger:4317`. | `jaeger` + runtime env |
| `policies/example.rego` | Permissive starter policy consumed by the OPA overlay. |
| `data/` | Bind-mount persistence for Redis + Langfuse Postgres. `.gitignore`d. |

## Quickstart — localhost mode (30 seconds)

```bash
docker compose -f docker-compose.localhost.yml up --build -d
curl -s http://localhost:8080/healthz                 # → {"status":"Healthy"}
curl -s http://localhost:8080/openapi/v1.json | head  # full v0.11 spec
docker compose -f docker-compose.localhost.yml down
```

Everything runs in a single container. Grain state evaporates on `down` —
use the clustered base for restart-survival.

## Clustered mode — single replica

```bash
docker compose -f docker-compose.clustered.yml up --build -d
docker compose -f docker-compose.clustered.yml logs -f runtime | grep "silo .* active"
# ~14s median, 30s P99 before /readyz flips green.
curl -s http://localhost:8080/readyz                 # → {"status":"Healthy"}
```

Silo membership, grain storage, and stream publication all land in Redis. Pull
the Redis dump from `./data/redis/` to snapshot state between runs.

## Clustered mode — 3-replica smoke test

The single-replica base publishes `8080:8080` to the host, which conflicts
under `--scale runtime=3`. Drop the host port by adding a local override:

```bash
cat > docker-compose.override.yml <<'EOF'
services:
  runtime:
    ports: []
EOF

docker compose -f docker-compose.clustered.yml -f docker-compose.override.yml \
    up --build --scale runtime=3 -d

# Orleans membership converges in ~60s max. Tail any replica's log:
docker compose -f docker-compose.clustered.yml -f docker-compose.override.yml logs -f

# Query a specific replica via its internal gateway port:
docker compose -f docker-compose.clustered.yml -f docker-compose.override.yml \
    exec -T --index=1 runtime wget -qO- http://localhost:8080/readyz
docker compose -f docker-compose.clustered.yml -f docker-compose.override.yml \
    exec -T --index=2 runtime wget -qO- http://localhost:8080/readyz
docker compose -f docker-compose.clustered.yml -f docker-compose.override.yml \
    exec -T --index=3 runtime wget -qO- http://localhost:8080/readyz

# All three should report Healthy. To round-robin against the cluster from
# the host, front it with an nginx/traefik service or use the Helm chart's
# ClusterIP service (PR 3).
```

**Pass criteria:** all 3 replicas report `SiloStatus.Active` within 60s, and
`/readyz` returns 200 on each.

## Layering overlays

Overlays compose orthogonally. Add any combination with additional `-f`
flags:

```bash
# localhost + OPA (policy-gated single-node)
docker compose \
    -f docker-compose.localhost.yml \
    -f docker-compose.opa.yml \
    up --build -d

# clustered + OPA + Langfuse + OTel (full-fat evaluation stack)
docker compose \
    -f docker-compose.clustered.yml \
    -f docker-compose.opa.yml \
    -f docker-compose.langfuse.yml \
    -f docker-compose.otel.yml \
    up --build -d
```

### OPA overlay

Mounts `./policies/` into the OPA sidecar read-only. The shipped
`example.rego` is an allow-all starter; swap it for one of the richer
samples under [`../../samples/opa-policies/`](../../samples/opa-policies/)
for real gating:

```bash
cp ../../samples/opa-policies/tenant-scoped-allow.rego ./policies/
# OPA --watch picks up the change within a second. No runtime restart needed.
```

Verify OPA is live:

```bash
curl -s http://$(docker compose \
    -f docker-compose.localhost.yml -f docker-compose.opa.yml \
    port opa 8181)/v1/data/vais/agents/allow
# → {"result":{"allowed":true}}
```

### Langfuse overlay

First run creates the Postgres schema; sign up at `http://localhost:3001`,
create a project, copy the public + secret keys into `runtime`'s env vars
(add them to a `docker-compose.override.yml` — do **not** commit the
secrets), and restart the runtime. `VAIS_LANGFUSE_PROJECT` defaults to
`vais-agents-dev`; change it to whatever you named the project.

### OTel overlay

Jaeger all-in-one is the lightest dev-time OTLP consumer. Open
`http://localhost:16686`, pick service `vais-agents-runtime`, and query for
traces. To ship to a production collector instead, override
`OTEL_EXPORTER_OTLP_ENDPOINT` on `runtime` via an `override.yml`.

## Teardown

```bash
docker compose -f docker-compose.clustered.yml down -v     # remove volumes
rm -rf ./data                                               # wipe bind mounts
```

`-v` drops named volumes (none in these files today — all state lives in
`./data/` bind mounts). `rm -rf ./data` wipes Redis + Langfuse state between
runs. The `data/` tree is `.gitignore`d.

## Known limitations (v0.16-preview)

- **Invoke returns 501.** Manifest-driven agent instantiation is deferred to
  Pillar B / v0.17 — `vais apply -f agent.yaml` followed by
  `vais invoke foo --text "hi"` returns
  `501 urn:vais-agents:agent-not-instantiable`. This is documented
  behaviour, not a bug.
- **Scale>1 needs a port override.** Base file publishes `8080:8080`;
  drop it as shown above when scaling past one replica.
- **Postgres clustering is not a compose recipe yet.** Redis is the default
  because the Orleans Redis streaming provider works; Postgres clustering
  degrades to in-silo memory streams (known limitation — see the pillar
  plan §Risks). Override the base's `runtime.environment` if you need to
  experiment with it.
- **OPA sample is allow-all.** Don't leave it as-is past the first smoke
  test — the runtime is default-open whenever OPA isn't configured, so
  this overlay with the stock policy is equivalent to no gating.

## Related

- [`../../src/Vais.Agents.Runtime.Host/`](../../src/Vais.Agents.Runtime.Host/)
  — source + Dockerfile for the container.
- [`../helm/vais-agents-runtime/`](../helm/vais-agents-runtime/) — Helm
  chart (PR 3 of Pillar A; lands next).
- [`../../samples/opa-policies/`](../../samples/opa-policies/) —
  production-grade Rego samples to swap in under `policies/`.
