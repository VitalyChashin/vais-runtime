# Wire Langfuse

You'll point the runtime's OTel pipeline at Langfuse, configure the project label, and see your first agent trace appear in the Langfuse UI. End state: every `vais invoke` produces a trace in Langfuse with model, tokens, latency, and the agent's conversation flow.

## Why Langfuse?

Langfuse is an LLM-specific observability UI. It reads OpenTelemetry OTLP output and maps `langfuse.*` tag names into a UI tuned for LLM workflows — trace timelines, token cost, prompt/completion inspection, user-scoped views. The runtime emits standard OTel `gen_ai.*` spans plus `langfuse.*` enrichment tags out of the box; Langfuse needs only the OTLP endpoint and project credentials.

## Prerequisites

- A running `vais-agents-runtime` ([Docker](deploy-runtime-on-docker.md) or [Kubernetes](deploy-runtime-on-kubernetes.md)).
- A Langfuse instance — either the bundled Compose overlay (dev) or a managed / self-hosted production deployment.

## Docker — overlay quickstart

The repo ships a `docker-compose.langfuse.yml` overlay that brings up Langfuse alongside the runtime and wires the OTel pipeline.

```bash
cd agentic/deploy/compose

docker compose \
  -f docker-compose.localhost.yml \
  -f docker-compose.langfuse.yml \
  up --build -d
```

The Langfuse UI is at `http://localhost:3001`. First-run setup:

1. Sign up (creates the first admin user).
2. Create a project (call it `dev` for now).
3. In project settings, copy the **public key** and **secret key**.
4. Drop them into a local `override.yml` — do **not** commit:

```yaml
# docker-compose.override.yml (gitignored)
services:
  runtime:
    environment:
      VAIS_LANGFUSE_PUBLIC_KEY: pk-lf-...
      VAIS_LANGFUSE_SECRET_KEY: sk-lf-...
      VAIS_LANGFUSE_HOST: http://langfuse:3000
      VAIS_LANGFUSE_PROJECT: dev
```

Restart:

```bash
docker compose \
  -f docker-compose.localhost.yml \
  -f docker-compose.langfuse.yml \
  -f docker-compose.override.yml \
  up -d
```

## Drive a turn — see a trace

```bash
vais apply -f greeter.yaml         # see Your first declarative agent
vais invoke greeter --text "Hi."
```

Open `http://localhost:3001`. The most recent trace shows:

- The chat span (renamed `chat {model}` once the response returns).
- `gen_ai.system` (provider), `gen_ai.response.model`, `gen_ai.usage.input_tokens`, `gen_ai.usage.output_tokens`.
- `vais.agent.name`, `vais.user.id`, `vais.tenant.id`, `vais.correlation.id` from `AgentContext` if set.
- `langfuse.trace.name`, `langfuse.user.id`, `langfuse.session.id`, `langfuse.tags`.

## Kubernetes — Helm values

Production Langfuse is managed externally (Langfuse Cloud or your own self-hosted deployment). Point the runtime at it:

```bash
kubectl create secret generic vais-langfuse -n vais \
  --from-literal=public-key='pk-lf-...' \
  --from-literal=secret-key='sk-lf-...'

helm upgrade vais-runtime ./deploy/helm/vais-agents-runtime \
  --namespace vais --reuse-values \
  --set observability.langfuse.host=https://cloud.langfuse.com \
  --set observability.langfuse.project=prod-agents \
  --set observability.langfuse.existingSecret=vais-langfuse
```

The chart maps the Secret keys to `VAIS_LANGFUSE_PUBLIC_KEY` and `VAIS_LANGFUSE_SECRET_KEY` on the runtime container.

## What the runtime emits

Per turn:

| Type | Name | Notable tags |
|---|---|---|
| Activity (span) | `chat` → `chat {model}` | `gen_ai.system`, `gen_ai.response.model`, `gen_ai.usage.input_tokens`, `gen_ai.usage.output_tokens` |
| Histogram | `gen_ai.client.token.usage` | unit `{token}`, split by `gen_ai.token.type` (input/output) |
| Histogram | `gen_ai.client.operation.duration` | unit `s`, with `error.type` dimension on failure |
| Enrichment tags | (on the active span) | `langfuse.trace.name`, `langfuse.user.id`, `langfuse.session.id`, `langfuse.tags`, `langfuse.trace.metadata.*` |

A cancelled `AskAsync` does **not** emit a `UsageRecord` — cancellations are not errors and shouldn't inflate error metrics. The span still closes (with `Ok` if the cancellation happened during a normal final-turn path, `Error` only if an actual exception surfaced).

Full reference: [Reference → Telemetry keys](../reference/telemetry-keys.md).

## Configuring static metadata

Useful when you want every trace tagged with deployment region, environment, or release version:

```yaml
# docker-compose.override.yml
services:
  runtime:
    environment:
      VAIS_LANGFUSE_DEFAULT_TAGS: "vais-agents,production"
      VAIS_LANGFUSE_METADATA: |
        deployment.region=eu-west-1
        deployment.version=2026-05-13
```

Helm values mirror the env vars: `observability.langfuse.defaultTags` and `observability.langfuse.metadata`.

## Things that catch people

- **Streaming turns may skip filter enrichment** depending on runtime version — check the [observability concept](../concepts/observability.md) for the current state.
- **`langfuse.user.id` requires `AgentContext.UserId` to be set.** The runtime's HTTP middleware can pull it from the JWT (`sub` claim) when OIDC auth is wired; otherwise it stays unset.
- **Don't commit Langfuse keys.** Keep them in `.env` (gitignored) or Kubernetes Secrets.

## What you built

- A runtime emitting `gen_ai.*` + `langfuse.*` OTel data on every agent turn.
- A Langfuse project receiving traces with model, tokens, latency, and conversation flow.
- A configurable enrichment layer for deployment metadata, environment tags, and user/session correlation.

## Next

- **[Wire Prometheus + Grafana](wire-prometheus-and-grafana.md)** — metrics + dashboards alongside Langfuse traces.
- [Concepts → Observability](../concepts/observability.md) — the full OTel pipeline.
- [Reference → Telemetry keys](../reference/telemetry-keys.md) — every `vais.*` and `langfuse.*` tag.
- [Sample → `ObservabilityOtelConsole`](../../samples/ObservabilityOtelConsole/) — library-mode OTel wiring.
