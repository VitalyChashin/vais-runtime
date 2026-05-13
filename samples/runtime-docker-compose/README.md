# runtime-docker-compose

Start the `vais-agents-runtime` using docker-compose. Two base configurations + three orthogonal overlays.

**Concepts:** [install the runtime](../../docs/devops/deploy-runtime-on-docker.md), [install guide (full)](../../docs/guides/install-the-runtime-locally.md), [runtime configuration](../../docs/reference/runtime-configuration.md).
**Needs API key:** no (runtime starts without one; agents that call LLMs need `OPENAI_API_KEY` etc.).
**Code:** 0 lines — configuration only.

---

## Quickstart — localhost mode

```bash
# From repo root:
cd oss/agentic

docker build -f src/Vais.Agents.Runtime.Host/Dockerfile -t vais-agents-runtime:local .

docker compose -f deploy/compose/docker-compose.base.localhost.yaml up -d

curl http://localhost:8080/healthz   # → ok
curl http://localhost:8080/readyz    # → ready
```

All state is in-memory (memory grain storage + memory streams). Zero external deps.

---

## Clustered mode

```bash
docker compose \
  -f deploy/compose/docker-compose.base.clustered.yaml \
  up -d
```

Adds Redis for Orleans clustering + grain storage + streams. Starts three runtime replicas and one Redis container.

---

## Overlays

Apply overlays on top of either base:

```bash
# OPA policy sidecar
docker compose \
  -f deploy/compose/docker-compose.base.localhost.yaml \
  -f deploy/compose/docker-compose.overlay.opa.yaml \
  up -d

# Langfuse tracing
docker compose \
  -f deploy/compose/docker-compose.base.localhost.yaml \
  -f deploy/compose/docker-compose.overlay.langfuse.yaml \
  up -d

# OpenTelemetry → Jaeger
docker compose \
  -f deploy/compose/docker-compose.base.localhost.yaml \
  -f deploy/compose/docker-compose.overlay.otel.yaml \
  up -d

# All three
docker compose \
  -f deploy/compose/docker-compose.base.localhost.yaml \
  -f deploy/compose/docker-compose.overlay.opa.yaml \
  -f deploy/compose/docker-compose.overlay.langfuse.yaml \
  -f deploy/compose/docker-compose.overlay.otel.yaml \
  up -d
```

---

## The compose files live in `deploy/compose/`

This sample is a pointer, not a copy. All YAML lives under [`deploy/compose/`](../../deploy/compose/) so you always edit one place. See [`deploy/compose/README.md`](../../deploy/compose/README.md) for the full overlay matrix and environment-variable reference.

---

## Environment variables

| Variable | Default | Purpose |
|---|---|---|
| `VAIS_MODE` | `localhost` | `localhost` or `clustered` |
| `VAIS_REDIS_CONNECTION` | — | Required for `clustered` mode |
| `OPENAI_API_KEY` | — | Needed by declarative agents that use OpenAI |
| `ANTHROPIC_API_KEY` | — | Needed by declarative agents that use Anthropic |
| `VAIS_OPA_BASE_URL` | — | Activates OPA policy enforcement |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | — | Activates OTel export |
| `VAIS_LANGFUSE_PROJECT` | — | Activates Langfuse enrichment |

Full reference: [`docs/reference/runtime-configuration.md`](../../docs/reference/runtime-configuration.md).

---

## Stop

```bash
docker compose -f deploy/compose/docker-compose.base.localhost.yaml down
```

Add `-v` to also remove Redis volumes.
