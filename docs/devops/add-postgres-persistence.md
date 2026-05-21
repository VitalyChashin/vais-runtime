# Add Postgres persistence

You'll connect the `vais-agents-runtime` container to a Postgres database for Orleans clustering and grain storage. End state: deployed agents, graphs, and conversation state survive runtime restarts — and you keep your existing Postgres operational practice (backups, replication, monitoring).

## Why Postgres?

Postgres is the right backing if your org already operates Postgres at scale and prefers it over Redis. The runtime supports Postgres for clustering + grain storage; what Postgres does **not** ship is an Orleans streams provider — so the event bus stays on memory streams (single-process effective scope). For production multi-silo with cross-silo event fan-out, pair Postgres grain storage with Redis streams, or accept the in-silo scope.

## Prerequisites

- A running `vais-agents-runtime` ([Docker](deploy-runtime-on-docker.md) or [Kubernetes](deploy-runtime-on-kubernetes.md)).
- Postgres 14+ reachable from the runtime; the runtime user needs `CREATE TABLE` rights for first-time schema setup.

## 1. Apply the Orleans schema (one-time)

Orleans ADO.NET providers need their own tables. The SQL lives in the Orleans repo:

```bash
curl -o /tmp/pg-main.sql \
  https://raw.githubusercontent.com/dotnet/orleans/v10.1.0/src/AdoNet/Orleans.AdoNet.Core/PostgreSQL-Main.sql
curl -o /tmp/pg-persistence.sql \
  https://raw.githubusercontent.com/dotnet/orleans/v10.1.0/src/AdoNet/Orleans.Persistence.AdoNet/PostgreSQL-Persistence.sql

psql postgresql://vais:vais@pg.internal:5432/vais_agents -f /tmp/pg-main.sql
psql postgresql://vais:vais@pg.internal:5432/vais_agents -f /tmp/pg-persistence.sql
```

Pin the URL to the Orleans 10.1 tag explicitly — HEAD may introduce schema deltas you didn't expect.

## 2. Docker — env var configuration

Two env vars on the runtime container — clustering mode and the connection string:

```yaml
# docker-compose.override.yml
services:
  runtime:
    environment:
      VAIS_HOSTING_MODE: clustered
      VAIS_POSTGRES_CONNECTION: "Host=pg.internal;Port=5432;Database=vais_agents;Username=vais;Password=..."
```

Or for localhost-mode-with-durable-storage (single-silo, but state survives restart — useful for dev with persistence):

```yaml
    environment:
      VAIS_HOSTING_MODE: localhost
      VAIS_LOCALHOST_PERSISTENCE: postgres
      VAIS_POSTGRES_CONNECTION: "Host=pg.internal;..."
```

Bring it up:

```bash
docker compose -f docker-compose.clustered.yml -f docker-compose.override.yml up -d
```

In localhost-with-postgres mode, only `PostgreSQL-Persistence.sql` is required (clustering stays in-memory; you can skip `PostgreSQL-Main.sql`).

## 3. Kubernetes — Helm values

```bash
# Create the Secret with your connection string
kubectl create secret generic vais-postgres -n vais \
  --from-literal=connection-string='Host=pg.data.svc.cluster.local;Port=5432;Database=vais_agents;Username=vais;Password=...'

# Install or upgrade
helm upgrade vais-runtime ./deploy/helm/vais-agents-runtime \
  --namespace vais --reuse-values \
  --set hosting.mode=clustered \
  --set clustering.backend=postgres \
  --set clustering.existingSecret=vais-postgres \
  --set replicaCount=3
```

## 4. Verify durability

```bash
vais invoke greeter --text "Remember this: the project is codename Falcon."
# (model responds; opaque state persisted to OrleansStorage table)

docker compose restart runtime   # OR  kubectl rollout restart deployment/vais-runtime -n vais

vais invoke greeter --text "What was the codename?"
# → "Falcon"
```

Inspect the storage table directly if you're curious:

```sql
SELECT grainidextensionstring, payloadjson
  FROM orleansstorage
 WHERE grainidextensionstring LIKE 'greeter%';
```

The `grainidextensionstring` column carries the grain key (`{agentId}/{sessionId}` for session grains).

## Streams — a known limitation

`Microsoft.Orleans.Streaming.AdoNet` doesn't have a 10.x build (only 9.x alphas). By default the runtime's event bus stays on memory streams when clustering is Postgres-only — events fan out within a silo, not across. Two production paths:

- **Hybrid: Postgres for grain storage, Redis for streams.** Set `VAIS_CLUSTERING_BACKEND=postgres` + `VAIS_POSTGRES_CONNECTION` for membership/grain state, then opt into cross-silo events with `VAIS_STREAMING_BACKEND=redis` + `VAIS_REDIS_CONNECTION`. The runtime keeps Postgres clustering/storage and uses the Redis stream provider for `OrleansAgentEventBus` / `OrleansAgentGraphEventBus`. (`Microsoft.Orleans.Streaming.Redis` is alpha — see [Add Redis persistence](add-redis-persistence.md).) The Azure-grade alternative is Event Hubs streams.
- **Accept single-silo event scope.** Leave `VAIS_STREAMING_BACKEND` unset (defaults to memory streams under Postgres clustering). Fine if you don't need cross-silo events (most single-tenant deployments).

## Things that catch people

- **Schema scripts change per Orleans version.** Pin the URL to the exact tag (`v10.1.0` here). HEAD evolves.
- **Reminder tables not included.** `Microsoft.Orleans.Reminders.AdoNet` is a separate package + SQL. Vais.Agents grains don't use reminders, so you can ignore this unless your own code does.
- **Password handling.** Avoid inline passwords in `values.yaml` — use a Secret. For Docker Compose, use `.env` files (gitignored) and reference via `${VAIS_POSTGRES_CONNECTION}`.

## What you built

- A Postgres-backed runtime where grain state persists in `OrleansStorage` rows.
- State survives runtime restarts and pod rolls.
- Operational visibility into agent state via standard SQL — useful for debugging and migration.

## Next

- **[Add Redis persistence](add-redis-persistence.md)** — alternative backing, or pair with Postgres for streams.
- **[Wire Langfuse](wire-langfuse.md)** — observability layered on top.
- [Concepts → Persistence](../concepts/persistence.md) — the full storage model and trade-offs.
- [Sample → `OrleansPostgresPersistence`](../../samples/OrleansPostgresPersistence/) — library-mode Postgres setup.
