# DevOps / admin

Audience: you're running the runtime.

You'll stand up the `vais-agents-runtime` container locally with Docker Compose, scale it to Kubernetes with the bundled Helm chart, attach Redis or Postgres for durability, and wire Langfuse and Prometheus for observability.

## Path

1. **[Deploy runtime on Docker](../guides/install-the-runtime-locally.md)** — docker-compose recipes: localhost, clustered, with OPA / Langfuse / OTel overlays.
2. **[Deploy runtime on Kubernetes](../guides/deploy-the-runtime-to-kubernetes.md)** — Helm chart walkthrough: kind quickstart → production with external Redis.
3. **[Add Redis persistence](../guides/add-redis-persistence.md)** — Orleans grain state on Redis.
4. **[Add Postgres persistence](../guides/add-postgres-persistence.md)** — durable backing for grain storage, agent registry, run history.
5. **[Wire Langfuse](../guides/deploy-otel-and-langfuse.md)** — OTel collector + Langfuse enrichment for agent traces.
6. **Wire Prometheus + Grafana** — *coming in Phase 3 of the docs reorganization.* Scrape config, starter dashboard JSON, and the `vais.*` metrics that matter.

## After this section

- Understanding what's running where → [Concepts → Architecture](../concepts/architecture.md)
- Tuning observability → [Concepts → Observability](../concepts/observability.md)
- Operating the control plane from the CLI → [Concepts → CLI](../concepts/cli.md)

## Related

- [Reference → Runtime configuration](../reference/runtime-configuration.md) — every env var + `appsettings.json` knob for the runtime container.
- [Reference → Telemetry keys](../reference/telemetry-keys.md) — every `vais.*` OTel tag emitted by the runtime.
- [Concepts → Persistence](../concepts/persistence.md) — Orleans, Redis, Postgres, vector data.
- [Concepts → Kubernetes operator](../concepts/kubernetes-operator.md) — `vais.io/v1alpha1` CRDs and reconciliation.
