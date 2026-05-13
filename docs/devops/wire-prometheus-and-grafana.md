# Wire Prometheus + Grafana

You'll configure Prometheus to scrape the runtime's `/metrics` endpoint, drop a starter dashboard JSON into Grafana, and see live request rate, latency, and token-usage panels for your agents. End state: a Grafana dashboard showing per-model request rate, p95 latency, and prompt/completion token throughput, updating in real time as you invoke agents.

## What the runtime exposes

When the `Prometheus` LLM gateway middleware is enabled (see **[Wire the LLM gateway](../agent-developer/wire-the-llm-gateway.md)**), the runtime exposes Prometheus-format metrics at `GET /metrics` on port 8080.

The metrics that matter for an agent fleet:

| Metric | Type | Labels | What it tells you |
|---|---|---|---|
| `llm_requests_total` | Counter | `model`, `workspace`, `status` | Request volume per model; `status` = `success` or `error` |
| `llm_request_duration_seconds` | Histogram | `model`, `workspace` | Wall-clock latency distribution (use `_bucket` for p50/p95/p99) |
| `llm_tokens_total` | Counter | `model`, `workspace`, `type` | Cumulative token usage; `type` = `prompt` or `completion` |
| `tool_calls_total` | Counter | `tool`, `workspace`, `status` | Tool invocations through the MCP gateway |
| `tool_call_duration_seconds` | Histogram | `tool`, `workspace` | Tool latency distribution |

Standard ASP.NET Core + Orleans process metrics ship alongside (`http_requests_*`, `grain_call_*`, `dotnet_*`, `process_*`).

Full reference: [Telemetry keys](../reference/telemetry-keys.md).

## Prerequisites

- A running `vais-agents-runtime` ([Docker](deploy-runtime-on-docker.md) or [Kubernetes](deploy-runtime-on-kubernetes.md)).
- An `LlmGatewayConfig` with the `Prometheus` middleware in the chain — without it, the LLM metrics aren't emitted. See **[Wire the LLM gateway](../agent-developer/wire-the-llm-gateway.md)**.
- Prometheus 2.50+ and Grafana 10+ (any deployment: Docker, in-cluster, hosted).

## 1. Verify the endpoint

```bash
curl -s http://localhost:8080/metrics | head -20
# # HELP llm_requests_total Number of LLM requests.
# # TYPE llm_requests_total counter
# llm_requests_total{model="gpt-4o-mini",workspace="_default",status="success"} 7
# ...
```

If the response is empty or 404, the `Prometheus` middleware isn't in the active `LlmGatewayConfig`. Apply a gateway config with `name: Prometheus` in the middleware list and re-bind your agents.

## 2. Configure Prometheus to scrape

### Docker — `prometheus.yml`

```yaml
# prometheus.yml
global:
  scrape_interval: 15s

scrape_configs:
  - job_name: vais-runtime
    metrics_path: /metrics
    static_configs:
      - targets:
          - vais-runtime:8080
```

Add a Prometheus service to your compose stack:

```yaml
services:
  prometheus:
    image: prom/prometheus:v2.50.0
    volumes:
      - ./prometheus.yml:/etc/prometheus/prometheus.yml:ro
    ports:
      - "9090:9090"
    depends_on:
      - runtime
```

Bring up:

```bash
docker compose up -d prometheus
open http://localhost:9090/targets   # vais-runtime should be UP
```

### Kubernetes — ServiceMonitor (Prometheus Operator)

```yaml
apiVersion: monitoring.coreos.com/v1
kind: ServiceMonitor
metadata:
  name: vais-runtime
  namespace: vais
  labels:
    release: prometheus    # match your Prometheus Operator's selector
spec:
  selector:
    matchLabels:
      app.kubernetes.io/name: vais-agents-runtime
  endpoints:
    - port: http
      path: /metrics
      interval: 15s
```

Apply:

```bash
kubectl apply -f vais-runtime-servicemonitor.yaml
```

If you're not running Prometheus Operator, use a scrape annotation on the runtime Service instead and let Prometheus' Kubernetes service-discovery pick it up:

```yaml
metadata:
  annotations:
    prometheus.io/scrape: "true"
    prometheus.io/path: "/metrics"
    prometheus.io/port: "8080"
```

(The Helm chart accepts `service.annotations` to inject these without editing the template.)

## 3. Starter dashboard

Save as `vais-agents-starter.json` and import into Grafana (Dashboards → Import → Upload JSON).

```json
{
  "title": "Vais.Agents — Starter",
  "panels": [
    {
      "title": "LLM request rate (per model)",
      "type": "timeseries",
      "targets": [{
        "expr": "sum by (model) (rate(llm_requests_total[1m]))",
        "legendFormat": "{{model}}"
      }]
    },
    {
      "title": "LLM request p95 latency",
      "type": "timeseries",
      "targets": [{
        "expr": "histogram_quantile(0.95, sum by (model, le) (rate(llm_request_duration_seconds_bucket[5m])))",
        "legendFormat": "{{model}} p95"
      }]
    },
    {
      "title": "Token throughput",
      "type": "timeseries",
      "targets": [{
        "expr": "sum by (model, type) (rate(llm_tokens_total[1m]))",
        "legendFormat": "{{model}} {{type}}"
      }]
    },
    {
      "title": "Error rate",
      "type": "stat",
      "targets": [{
        "expr": "sum(rate(llm_requests_total{status=\"error\"}[5m])) / sum(rate(llm_requests_total[5m]))",
        "legendFormat": "error %"
      }]
    },
    {
      "title": "Tool call rate",
      "type": "timeseries",
      "targets": [{
        "expr": "sum by (tool) (rate(tool_calls_total[1m]))",
        "legendFormat": "{{tool}}"
      }]
    }
  ]
}
```

The above is a Prometheus data-source dashboard with five panels: request rate per model, p95 latency, token throughput (split by prompt/completion), error rate, tool call rate. Treat it as a starting point; production dashboards layer on per-workspace splits, cost calculations (tokens × model price), and SLO panels.

## 4. Drive load and watch

```bash
# In one terminal — invoke the agent in a loop
for i in {1..100}; do
  vais invoke greeter --text "request $i"
done
```

Open the Grafana dashboard. Within ~15 s (one scrape interval) you should see:

- Request rate climbs.
- p95 latency reflects model + network latency.
- Token throughput follows the prompt/completion split — usually prompt > completion for short Q&A.
- Error rate stays at 0 unless you trip the rate limiter.

## What you built

- A Prometheus scrape target on the runtime emitting LLM and tool metrics.
- A starter Grafana dashboard with five panels covering the most-load-bearing signals.
- A foundation for production-grade dashboards — add per-workspace splits, cost, SLO burn-rate panels as your fleet grows.

## Next

- **[Wire Langfuse](wire-langfuse.md)** — LLM-specific UI views alongside the Prometheus + Grafana stack.
- [Reference → Telemetry keys](../reference/telemetry-keys.md) — every `vais.*` OTel tag + Prometheus metric.
- [Concepts → Observability](../concepts/observability.md) — the full pipeline (traces + metrics + logs).
- [Wire the LLM gateway](../agent-developer/wire-the-llm-gateway.md) — must include the `Prometheus` middleware to emit the LLM metrics this dashboard reads.
