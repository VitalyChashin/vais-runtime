# Add Redis persistence

You'll connect the `vais-agents-runtime` container to a Redis instance for Orleans clustering, grain storage, and event-bus streaming. End state: the runtime survives restarts, multiple replicas share state, and deployed agents and graphs persist across pod rolls.

## Why Redis?

Redis is the default backing for clustered mode. It handles three Orleans concerns at once: silo-to-silo membership, grain state persistence, and the event-bus stream transport. The Orleans 10.x family has the most mature Redis support; Postgres ships clustering and storage but no streams provider (see **[Add Postgres persistence](add-postgres-persistence.md)** for a hybrid setup).

## Prerequisites

- A running `vais-agents-runtime` ([Docker](deploy-runtime-on-docker.md) or [Kubernetes](deploy-runtime-on-kubernetes.md)).
- Redis 7+ reachable from the runtime container or pod.

## Docker — point at an existing Redis

The `docker-compose.clustered.yml` recipe bundles a Redis 7-alpine service for evaluation. For a real deployment, point the runtime at an existing Redis with an override:

```yaml
# docker-compose.override.yml
services:
  runtime:
    environment:
      VAIS_HOSTING_MODE: clustered
      VAIS_REDIS_CONNECTION: "redis.internal:6379"
      # Password-protected Redis:
      # VAIS_REDIS_CONNECTION: "redis.internal:6379,password=s3cret,ssl=false"
```

Bring it up:

```bash
docker compose \
  -f docker-compose.clustered.yml \
  -f docker-compose.override.yml \
  up -d
```

The runtime's HEALTHCHECK turns green within ~15 s once Redis connectivity converges. Inspect what's there:

```bash
redis-cli -h redis.internal KEYS 'vais.agents*' | head
# vais.agents/support/conv-1
# vais.agents/research-agent/main
# ...
```

Each `(agentId, sessionId)` pair produces a grain key prefixed with the storage name `vais.agents`. Treat the storage name as a permanent decision — renaming it orphans existing state.

## Kubernetes — Helm + Secret

The chart pulls the connection string from a Secret, not from `values.yaml`. This is non-negotiable for production — connection strings often contain passwords.

```bash
# Create the Secret with your connection string
kubectl create secret generic vais-redis -n vais \
  --from-literal=connection-string='redis-master.data.svc.cluster.local:6379'

# Point the chart at it
helm upgrade vais-runtime ./deploy/helm/vais-agents-runtime \
  --namespace vais --reuse-values \
  --set hosting.mode=clustered \
  --set clustering.backend=redis \
  --set clustering.existingSecret=vais-redis \
  --set replicaCount=3
```

The chart maps `clustering.existingSecret.connection-string` → the runtime container's `VAIS_REDIS_CONNECTION` env var.

## Verify durability

Drive a turn, restart the runtime, drive another turn — state must survive.

```bash
vais invoke greeter --text "Remember this: the password is 'hunter2'."
# (model responds; opaque state is persisted to Redis)

docker compose restart runtime   # OR  kubectl rollout restart deployment/vais-runtime -n vais

# Wait for /healthz → Healthy, then:
vais invoke greeter --text "What was the password?"
# → "hunter2"  (state rehydrated from Redis)
```

The conversation continues across the restart. This is the difference between localhost mode (memory-only) and clustered mode (Redis-backed).

## What goes into Redis

| Concern | Redis key prefix | Purpose |
|---|---|---|
| Orleans clustering | `vais-agents/Cluster/...` | Silo-to-silo membership; how silos find each other |
| Grain storage | `vais.agents/*` | Per-agent / per-session / per-graph state |
| Event-bus streams | `vais.agents.events/*` | Cross-silo event fan-out (`AgentGraphEvent`, etc.) |

`redis-cli FLUSHDB` wipes all three — useful in dev, catastrophic in production.

## Things that catch people

- **All silos need the same Redis.** Multi-replica deployments rely on Redis as the membership provider; pointing replicas at different Redis instances splits the cluster.
- **`Microsoft.Orleans.Streaming.Redis` ships as `10.1.0-alpha.1`** — the only published 10.x build. Behaviour is stable for our test scenarios but production deployments should pin and track upstream for a stable release.
- **Storage name is `vais.agents`** — fixed by `AiAgentGrain.StorageName`. Don't override without understanding the consequence for existing state.

## What you built

- A clustered-mode runtime backed by Redis for membership, state, and streaming.
- State that survives pod rolls and silo restarts.
- A scaling path — additional replicas attach to the same Redis and share state automatically.

## Next

- **[Add Postgres persistence](add-postgres-persistence.md)** — alternative or hybrid (Postgres for grain state, Redis for streams).
- **[Wire Langfuse](wire-langfuse.md)** — observability on top of the persistent runtime.
- [Concepts → Persistence](../concepts/persistence.md) — the full storage model.
- [Sample → `OrleansRedisPersistence`](../../samples/OrleansRedisPersistence/) — library-mode Redis setup, useful if you're embedding instead of running the runtime.
