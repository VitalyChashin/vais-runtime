# DevOps / admin

Audience: you're running the runtime.

You'll stand up the `vais-agents-runtime` container locally with Docker Compose, scale it to Kubernetes with the bundled Helm chart, attach Redis or Postgres for durability, and wire Langfuse and Prometheus for observability.

## Path

1. **[Deploy the runtime on Docker](deploy-runtime-on-docker.md)** — docker-compose recipes: localhost, clustered, with OPA / Langfuse / OTel overlays.
2. **[Deploy the runtime on Kubernetes](deploy-runtime-on-kubernetes.md)** — Helm chart walkthrough: kind quickstart → production with external Redis.
3. **[Add Redis persistence](add-redis-persistence.md)** — Orleans clustering, grain storage, and event-bus streams on Redis.
4. **[Add Postgres persistence](add-postgres-persistence.md)** — durable backing for grain storage with optional hybrid (Postgres + Redis streams).
5. **[Configure LLM providers](configure-llm-providers.md)** — `secret://` URIs, env vs file-backed keys, multi-provider isolation, custom endpoints (vLLM / Ollama / LiteLLM / Azure), Fallback pools.
6. **[Wire Langfuse](wire-langfuse.md)** — point the runtime's OTel pipeline at Langfuse for LLM-specific UI views.
7. **[Wire Prometheus + Grafana](wire-prometheus-and-grafana.md)** — scrape config, starter dashboard JSON, and the `vais.*` metrics that matter.

## After this section

- Understanding what's running where → [Concepts → Architecture](../concepts/architecture.md)
- Tuning observability → [Concepts → Observability](../concepts/observability.md)
- Operating the control plane from the CLI → [Concepts → CLI](../concepts/cli.md)

## Related

- [Reference → Runtime configuration](../reference/runtime-configuration.md) — every env var + `appsettings.json` knob for the runtime container.
- [Reference → Telemetry keys](../reference/telemetry-keys.md) — every `vais.*` OTel tag emitted by the runtime.
- [Concepts → Persistence](../concepts/persistence.md) — Orleans, Redis, Postgres, vector data.
- [Concepts → Kubernetes operator](../concepts/kubernetes-operator.md) — `vais.io/v1alpha1` CRDs and reconciliation.
