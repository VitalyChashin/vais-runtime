# Guide: add Postgres persistence

Swap in Postgres for Orleans clustering + grain storage. Postgres doesn't ship an Orleans-streaming provider in v0.4, so the event bus stays on memory streams (or Event Hubs).

## Start Postgres

```yaml
# docker-compose.yml
services:
  postgres:
    image: postgres:16-alpine
    ports:
      - "5432:5432"
    environment:
      POSTGRES_USER: vais
      POSTGRES_PASSWORD: vais
      POSTGRES_DB: vais_agents
```

```bash
docker-compose up -d postgres
```

## Apply Orleans schema

Orleans' ADO.NET providers need their tables (clustering + persistence). The SQL lives in the Orleans repo, not in our package — we don't redistribute it. Fetch the two scripts for your Orleans version (10.1) and run them:

```bash
# From inside the repo root:
curl -o /tmp/pg-main.sql       https://raw.githubusercontent.com/dotnet/orleans/v10.1.0/src/AdoNet/Orleans.AdoNet.Core/PostgreSQL-Main.sql
curl -o /tmp/pg-persistence.sql https://raw.githubusercontent.com/dotnet/orleans/v10.1.0/src/AdoNet/Orleans.Persistence.AdoNet/PostgreSQL-Persistence.sql

psql postgresql://vais:vais@localhost:5432/vais_agents -f /tmp/pg-main.sql
psql postgresql://vais:vais@localhost:5432/vais_agents -f /tmp/pg-persistence.sql
```

## Packages

```xml
<PackageReference Include="Vais.Agents.Persistence.Postgres" Version="0.4.0-preview" />
```

## Silo configuration

```csharp
using Vais.Agents.Persistence.Postgres;

const string PgConn = "Host=localhost;Port=5432;Username=vais;Password=vais;Database=vais_agents";

builder
    .UseAgenticPostgresClustering(PgConn)
    .AddAgenticPostgresGrainStorage(PgConn)
    .AddMemoryGrainStorage("PubSubStore")        // streams still use memory here
    .AddMemoryStreams("vais.agents.events");     // or Event Hubs in production
```

Both wrappers pre-set `Invariant = "Npgsql"`. If you need to build custom ADO.NET options yourself, use the `const string AgenticPostgresPersistenceExtensions.NpgsqlInvariant` so the invariant name isn't string-typed at your end.

## Client configuration

```csharp
clientBuilder
    .UseAgenticPostgresClustering(PgConn)
    .AddMemoryStreams("vais.agents.events");
```

## Verify

Same durability test as the Redis guide. Drive a turn; restart silo; drive another turn on same `(agentId, sessionId)`; confirm history survived:

```sql
SELECT grainidextensionstring, payloadjson
  FROM orleansstorage
 WHERE grainidextensionstring LIKE 'support%';
```

The `OrleansStorage` table's `grainidextensionstring` column carries your grain key (`{agentId}/{sessionId}` for session grains) — useful for sanity checks during dev.

## Things that catch people

- **Schema scripts change per Orleans version.** Pin the URL to the Orleans 10.1 tag explicitly — HEAD may introduce schema deltas you didn't expect.
- **`Orleans.Hosting` namespace has the extension methods.** The SDK's global usings cover it; no extra `using` in your silo configurator. (Contrast with Orleans Redis, which puts extensions in `Microsoft.Extensions.Hosting` — we wrap both so you don't have to remember.)
- **No streams provider in Postgres.** `Microsoft.Orleans.Streaming.AdoNet` has only 9.x alphas; at 10.x there's nothing. If you need Orleans streams with Postgres today, pair Postgres grain-storage with either Event Hubs streams (Azure) or Redis streams (dev / non-Azure).
- **Reminder tables not included.** `Microsoft.Orleans.Reminders.AdoNet` is separate. Add its package + a third SQL script if you use Orleans reminders elsewhere; `AiAgentGrain` / `AgentSessionGrain` / `AgentConfigGrain` don't.

## See also

- [Persistence concept](../concepts/persistence.md)
- [Run on Orleans locally](run-on-orleans-locally.md)
- [Add Redis persistence](add-redis-persistence.md)
- Sample: `samples/OrleansPostgresPersistence/` (per samples plan)
