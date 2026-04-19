# Guide: add Redis persistence

Swap in Redis for three Orleans concerns at once — cluster membership, grain storage, and the event-bus stream transport. Assumes you have [Orleans running locally](run-on-orleans-locally.md) and Docker available.

## Start Redis

```yaml
# docker-compose.yml
services:
  redis:
    image: redis:7-alpine
    ports:
      - "6379:6379"
```

```bash
docker-compose up -d redis
```

## Packages

Add `Vais.Agents.Persistence.Redis`:

```xml
<PackageReference Include="Vais.Agents.Persistence.Redis" Version="0.4.0-preview" />
```

## Silo configuration

```csharp
using Vais.Agents.Persistence.Redis;

const string RedisConn = "localhost:6379";

builder
    .UseAgenticRedisClustering(RedisConn)
    .AddAgenticRedisGrainStorage(RedisConn)   // session + config grains persist here
    .AddMemoryGrainStorage("PubSubStore")     // streams still need this (memory is fine)
    .UseAgenticRedisStreaming(RedisConn);     // event-bus transport
```

`UseAgenticRedisClustering` wraps Orleans' `UseRedisClustering` with `ConfigurationOptions.Parse` pre-applied. `AddAgenticRedisGrainStorage` registers the provider under `AiAgentGrain.StorageName` (= `"vais.agents"`), the storage name Vais.Agents grains expect, so consumers don't have to re-type it. `UseAgenticRedisStreaming` wraps `AddRedisStreams` under the `"vais.agents.events"` provider name.

## Client configuration

```csharp
clientBuilder
    .UseAgenticRedisClustering(RedisConn)
    .UseAgenticRedisStreaming(RedisConn);
```

Mirror the silo's stream config — the client must know how to reach the `"vais.agents.events"` provider to subscribe or publish.

## Verify durability

Drive a turn, restart the silo, drive another turn on the same `(agentId, sessionId)` — history should survive:

```csharp
var session = runtime.GetSession("support", "conv-1");
var agent = new StatefulAiAgent(provider, new StatefulAgentOptions { Session = session });
await agent.AskAsync("Remember this: the password is 'hunter2'.");

// <restart silo, reconnect client>

var session2 = runtime.GetSession("support", "conv-1");
var agent2 = new StatefulAiAgent(provider, new StatefulAgentOptions { Session = session2 });
Console.WriteLine(await agent2.AskAsync("What was the password?"));
// → "hunter2" (the silo rehydrated the grain state from Redis)
```

Inspect the Redis key directly if you're curious:

```bash
redis-cli KEYS '*'
# → "vais.agents/support/conv-1" or similar, depending on Orleans version
```

## Known constraint — Streaming.Redis is alpha

`Microsoft.Orleans.Streaming.Redis` ships as `10.1.0-alpha.1` — the only published 10.x build. Our package references it; behaviour is stable enough for the scenarios we test but consumers running this in production should pin deliberately and follow upstream for a stable release.

If you want to avoid the alpha, use Orleans' Event Hubs streaming or memory streams for dev:

```csharp
// Instead of UseAgenticRedisStreaming:
builder.AddMemoryStreams("vais.agents.events");               // dev only
// or
builder.AddEventHubStreams("vais.agents.events", ...);        // production Azure
```

## Things that catch people

- **Redis clustering discovery needs both silos to use the same Redis instance.** If you spin up multiple silos, point all of them at the same connection string — membership is the Redis clustering provider's job.
- **Grain storage keys live under Orleans' own prefix.** Renaming the `"vais.agents"` storage name means your old persisted state is orphaned. Treat the storage name as a permanent decision.
- **Clear the keys for a clean slate.** `redis-cli FLUSHDB` wipes clustering + state + stream state — useful during dev, catastrophic in production.

## See also

- [Persistence concept](../concepts/persistence.md)
- [Run on Orleans locally](run-on-orleans-locally.md)
- [Add Postgres persistence](add-postgres-persistence.md)
- Sample: `samples/OrleansRedisPersistence/` (per samples plan)
