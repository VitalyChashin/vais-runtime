# Wire Langfuse

You'll point the runtime's OTel pipeline at Langfuse, configure the project label, and see your first agent trace appear in the Langfuse UI. End state: every `vais invoke` produces a trace in Langfuse with model, tokens, latency, and the agent's conversation flow.

## How the runtime + Langfuse integration works today (v0.16)

Two distinct layers cooperate:

1. **Enrichment** — `LangfuseEnrichmentFilter` adds `langfuse.*` tags (`trace.name`, `user.id`, `session.id`, `tags`) to every active span. Enabled when `VAIS_LANGFUSE_PROJECT` is set.
2. **Export** — the standard OpenTelemetry exporter ships those spans to a collector or directly to Langfuse over OTLP. Enabled when `OTEL_EXPORTER_OTLP_ENDPOINT` (or `VAIS_OTEL_CONSOLE` for stdout debugging) is configured.

These are independent. Setting only `VAIS_LANGFUSE_PROJECT` produces enriched spans that go nowhere — the runtime logs a warning at startup:

> `[vais] WARNING: VAIS_LANGFUSE_PROJECT is set but neither VAIS_OTEL_ENDPOINT nor VAIS_OTEL_CONSOLE is configured. Langfuse traces will NOT be emitted.`

Credentials (Langfuse public-key / secret-key) are not handled by the runtime directly — they ride in the standard `OTEL_EXPORTER_OTLP_HEADERS` env var as a basic-auth `Authorization` header, just like any OTLP HTTP receiver. There are **no** `VAIS_LANGFUSE_PUBLIC_KEY` / `_SECRET_KEY` env vars and no Helm `langfuse.existingSecret` field today.

The two `VAIS_LANGFUSE_*` env vars that the runtime reads:

| Env var | Purpose |
|---|---|
| `VAIS_LANGFUSE_PROJECT` | Required. Project label stamped onto every span as `langfuse.metadata.project`. Triggers `LangfuseEnrichmentFilter` registration. |
| `VAIS_LANGFUSE_HOST` | Optional. Used only by the startup self-check (`{LangfuseHost}/api/health`). Not part of trace export. |

## Prerequisites

- A running `vais-agents-runtime` ([Docker](deploy-runtime-on-docker.md) or [Kubernetes](deploy-runtime-on-kubernetes.md)).
- A Langfuse instance — either the bundled Compose overlay (dev) or Langfuse Cloud / your own self-hosted deployment.

## Docker — local Langfuse v2 + enrichment-only

The repo ships a `docker-compose.langfuse.yml` overlay that brings up Langfuse v2 alongside the runtime. The overlay wires `VAIS_LANGFUSE_PROJECT` + `VAIS_LANGFUSE_HOST`; it does **not** wire OTLP export to Langfuse because Langfuse v2 has no native OTLP receiver (export to v2 requires a separate OpenTelemetry Collector with the Langfuse exporter plugin).

```bash
cd agentic/deploy/compose

docker compose \
  -f docker-compose.localhost.yml \
  -f docker-compose.langfuse.yml \
  up --build -d
```

The Langfuse UI is at `http://localhost:3001`. First-run setup creates the admin user and a project (the overlay uses `vais-agents-dev` for the project label). At this point spans are enriched with `langfuse.*` tags but **no traces flow into Langfuse** — verify the warning above appears in `docker compose logs runtime`.

To actually push traces in dev, the simplest path is to bypass Langfuse v2's missing receiver:

- **Use `VAIS_OTEL_CONSOLE=true`** to print spans to stdout for verification, or
- **Stand up a local OTel Collector** with the [Langfuse exporter community plugin](https://github.com/langfuse/langfuse-docs) between the runtime and Langfuse v2, or
- **Use Langfuse v3 (preview)** which has a native OTLP receiver at `/api/public/otel/v1/traces` — point `OTEL_EXPORTER_OTLP_ENDPOINT` at it (see the Langfuse Cloud / v3 pattern below).

## Docker — Langfuse Cloud (or v3 self-hosted)

Langfuse Cloud and Langfuse v3 expose a native OTLP/HTTP receiver, so the runtime can target them directly:

```yaml
# docker-compose.override.yml (gitignored)
services:
  runtime:
    environment:
      VAIS_LANGFUSE_PROJECT: prod-agents
      OTEL_EXPORTER_OTLP_ENDPOINT: https://cloud.langfuse.com/api/public/otel
      OTEL_EXPORTER_OTLP_PROTOCOL: http/protobuf
      OTEL_EXPORTER_OTLP_HEADERS: "Authorization=Basic ${LANGFUSE_AUTH_B64}"
```

Where `LANGFUSE_AUTH_B64` is `base64(public_key:secret_key)`. Build it once:

```bash
LANGFUSE_AUTH_B64=$(printf 'pk-lf-xxx:sk-lf-xxx' | base64 -w0)
```

Keep `pk-lf-*` / `sk-lf-*` in a gitignored `.env` and reference via `${LANGFUSE_AUTH_B64}` in the compose override.

## Drive a turn — see a trace

```bash
vais apply -f greeter.yaml         # see Your first declarative agent
vais invoke greeter --text "Hi."
```

Open your Langfuse UI. The most recent trace shows:

- The chat span (renamed `chat {model}` once the response returns).
- `gen_ai.system` (provider), `gen_ai.response.model`, `gen_ai.usage.input_tokens`, `gen_ai.usage.output_tokens`.
- `vais.agent.name`, `vais.user.id`, `vais.tenant.id`, `vais.correlation.id` from `AgentContext` if set.
- `langfuse.trace.name`, `langfuse.user.id`, `langfuse.session.id`, `langfuse.tags`.

## Kubernetes — Helm values

The chart wires `VAIS_LANGFUSE_PROJECT` + `OTEL_EXPORTER_OTLP_ENDPOINT` from values. For Langfuse Cloud or v3, point the OTel endpoint at the OTLP receiver:

```bash
helm upgrade vais-runtime ./deploy/helm/vais-agents-runtime \
  --namespace vais --reuse-values \
  --set observability.langfuse.project=prod-agents \
  --set observability.otel.endpoint=https://cloud.langfuse.com/api/public/otel
```

**Credentials.** The chart does not yet template `OTEL_EXPORTER_OTLP_HEADERS` (no `extraEnv` / `envFrom` hook). For now, inject the basic-auth header via a kustomize overlay or a post-render patch that adds the env var to the runtime container — referencing a `Secret` you created out-of-band:

```bash
kubectl create secret generic vais-langfuse -n vais \
  --from-literal=auth-header="Basic $(printf 'pk-lf-xxx:sk-lf-xxx' | base64 -w0)"
```

Then patch the deployment to add:

```yaml
- name: OTEL_EXPORTER_OTLP_HEADERS
  valueFrom:
    secretKeyRef:
      name: vais-langfuse
      key: auth-header
- name: OTEL_EXPORTER_OTLP_PROTOCOL
  value: http/protobuf
```

A native chart-level `extraEnv` / `envFrom` hook is on the roadmap; once it lands, this becomes a `--set extraEnv.OTEL_EXPORTER_OTLP_HEADERS=...` one-liner.

## What the runtime emits

Per turn:

| Type | Name | Notable tags |
|---|---|---|
| Activity (span) | `chat` → `chat {model}` | `gen_ai.system`, `gen_ai.response.model`, `gen_ai.usage.input_tokens`, `gen_ai.usage.output_tokens` |
| Histogram | `gen_ai.client.token.usage` | unit `{token}`, split by `gen_ai.token.type` (input/output) |
| Histogram | `gen_ai.client.operation.duration` | unit `s`, with `error.type` dimension on failure |
| Enrichment tags | (on the active span) | `langfuse.trace.name`, `langfuse.user.id`, `langfuse.session.id`, `langfuse.tags`, `langfuse.metadata.project` |

A cancelled `AskAsync` does **not** emit a `UsageRecord` — cancellations are not errors and shouldn't inflate error metrics. The span still closes (with `Ok` if the cancellation happened during a normal final-turn path, `Error` only if an actual exception surfaced).

Full reference: [Reference → Telemetry keys](../reference/telemetry-keys.md).

## Things that catch people

- **`VAIS_LANGFUSE_PROJECT` alone produces no traces.** Enrichment is independent of export — wire `OTEL_EXPORTER_OTLP_ENDPOINT` (or `VAIS_OTEL_CONSOLE=true` for stdout) too, otherwise the spans are dropped on the floor. The runtime startup log warns on this combination.
- **Streaming runs go through `IStreamingAgentFilter`, not `IAgentFilter`.** `LangfuseEnrichmentFilter` is an `IAgentFilter`, so streaming turns don't see the enrichment chain today. Wire equivalent enrichment in a custom streaming filter if you need it on streamed runs.
- **`langfuse.user.id` requires `AgentContext.UserId` to be set.** The runtime's HTTP middleware can pull it from the JWT (`sub` claim) when OIDC auth is wired; otherwise it stays unset.
- **Don't commit Langfuse keys.** Keep them in `.env` (gitignored) or Kubernetes Secrets.
- **No built-in `defaultTags` / `metadata` env-var binding.** To stamp deployment metadata, set them via `LangfuseEnrichmentOptions` in a custom composition root (`AddLangfuseEnrichment(o => o.StaticMetadata[...] = ...)`); no `VAIS_LANGFUSE_DEFAULT_TAGS` / `VAIS_LANGFUSE_METADATA` env-var binding is wired through `RuntimeOptions`.

## What you built

- A runtime emitting `gen_ai.*` + `langfuse.*` OTel data on every agent turn.
- A Langfuse project receiving traces with model, tokens, latency, and conversation flow.
- A configurable enrichment layer for deployment metadata, environment tags, and user/session correlation.

## Next

- **[Wire Prometheus + Grafana](wire-prometheus-and-grafana.md)** — metrics + dashboards alongside Langfuse traces.
- [Concepts → Observability](../concepts/observability.md) — the full OTel pipeline.
- [Reference → Telemetry keys](../reference/telemetry-keys.md) — every `vais.*` and `langfuse.*` tag.
- [Sample → `ObservabilityOtelConsole`](../../samples/ObservabilityOtelConsole/) — library-mode OTel wiring.
