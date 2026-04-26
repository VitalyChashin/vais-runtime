# Install the runtime (5-minute quickstart)

Get a running `vais-agents-runtime` container on your laptop in five minutes. This page is the short path — see [install the runtime locally (full guide)](../guides/install-the-runtime-locally.md) for the complete walkthrough including clustered mode, OPA, and observability overlays.

## What you need

- Docker Engine 24+ or Docker Desktop 4.30+ (Compose v2 required).
- The OSS repo checked out locally.

## Step 1 — Build the image

```bash
cd oss/agentic

docker build \
  -f src/Vais.Agents.Runtime.Host/Dockerfile \
  -t vais-agents-runtime:local \
  .
```

This takes ~2 minutes on first build (restores NuGet packages); subsequent builds use the layer cache and take ~20 seconds.

## Step 2 — Start localhost mode

```bash
docker compose \
  -f deploy/compose/docker-compose.localhost.yml \
  up -d
```

This starts a single container on port 8080 with all state in memory (no Redis or Postgres needed).

## Step 3 — Verify

```bash
curl http://localhost:8080/healthz
# ok

curl http://localhost:8080/readyz
# ready

curl -s http://localhost:8080/openapi/v1.json | jq .info.title
# "Vais Agents Control Plane"
```

## Step 4 — Point the CLI at it

```bash
vais config set-context local --server http://localhost:8080
vais config use-context local
vais get
# (empty list — no agents registered yet)
```

## Stop

```bash
docker compose -f deploy/compose/docker-compose.localhost.yml down
```

## Next steps

- [Deploy your first agent](deploy-your-first-agent.md) — apply a YAML manifest and invoke it.
- [Full install guide](../guides/install-the-runtime-locally.md) — clustered mode, OPA, OTel, Langfuse.
- [Deploy to Kubernetes](../guides/deploy-the-runtime-to-kubernetes.md) — Helm chart walkthrough.
