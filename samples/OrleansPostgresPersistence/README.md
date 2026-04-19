# OrleansPostgresPersistence

Single-process silo backed by Postgres for clustering + grain storage. Streams stay on memory in v0.4 (no stable Postgres streams provider).

**Concepts:** [persistence](../../docs/concepts/persistence.md).
**Packages:** `Vais.Agents.Abstractions`, `Vais.Agents.Core`, `Vais.Agents.Hosting.Orleans`, `Vais.Agents.Persistence.Postgres`.
**Needs API key:** no.
**Needs Docker:** yes + Orleans schema applied manually.

## One-time setup

```bash
docker compose --file samples/OrleansPostgresPersistence/docker-compose.yml up -d

# Apply Orleans' Postgres schema (10.1 tag). See the guide for details.
curl -o /tmp/pg-main.sql        https://raw.githubusercontent.com/dotnet/orleans/v10.1.0/src/AdoNet/Orleans.AdoNet.Core/PostgreSQL-Main.sql
curl -o /tmp/pg-persistence.sql https://raw.githubusercontent.com/dotnet/orleans/v10.1.0/src/AdoNet/Orleans.Persistence.AdoNet/PostgreSQL-Persistence.sql

psql postgresql://vais:vais@localhost:5432/vais_agents -f /tmp/pg-main.sql
psql postgresql://vais:vais@localhost:5432/vais_agents -f /tmp/pg-persistence.sql
```

## Run

```bash
dotnet run --project samples/OrleansPostgresPersistence
# Teardown:
docker compose --file samples/OrleansPostgresPersistence/docker-compose.yml down -v
```

Override the connection string via `POSTGRES_CONN`. See [add Postgres persistence guide](../../docs/guides/add-postgres-persistence.md) for schema notes.
