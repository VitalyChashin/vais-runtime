# OrleansRedisPersistence

Same shape as `OrleansSilo` but swaps memory providers for Redis — clustering + grain storage + streams all on Redis. Requires Docker.

**Concepts:** [persistence](../../docs/concepts/persistence.md).
**Packages:** `Vais.Agents.Abstractions`, `Vais.Agents.Core`, `Vais.Agents.Hosting.Orleans`, `Vais.Agents.Persistence.Redis`.
**Needs API key:** no.
**Needs Docker:** yes — `docker compose up -d redis`.

```bash
docker compose --file samples/OrleansRedisPersistence/docker-compose.yml up -d
dotnet run --project samples/OrleansRedisPersistence
# When done:
docker compose --file samples/OrleansRedisPersistence/docker-compose.yml down
```

Override the connection via `REDIS_CONN` (defaults to `localhost:6379`). See the [add Redis persistence guide](../../docs/guides/add-redis-persistence.md) for streaming-alpha notes.
