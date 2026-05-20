# Persistence

Persistence in Vais.Agents means three things that commonly get confused:

1. **Agent / session state** — `IAgentSession.History`, per-agent config. Durable via Orleans grain storage.
2. **Orleans cluster membership** — silo discovery. Durable via Orleans' clustering provider.
3. **Event streams** — `IAgentEventBus` payloads across silos. Durable via Orleans' streaming provider.

Each has its own provider package; you pick the backend (Redis, Postgres, memory) per concern. Plus a separate `Vais.Agents.Persistence.VectorData` package for RAG's vector-store retrieval — related but orthogonal.

## Orleans host

`Vais.Agents.Hosting.Orleans` brings three grain types:

- **`IAiAgentGrain`** — legacy single-session execution grain (M3a). Exposes `AskAsync` on the silo. Kept for silo-local scenarios; most new code uses the client-side loop (below).
- **`IAgentSessionGrain`** — state container for a `(agentId, sessionId)` pair. Pure state: history, append, reset. No LLM turn loop. Grain key is `{agentId}/{sessionId}` (encoded via `OrleansSessionGrainKey`).
- **`IAgentConfigGrain`** — per-agent shared config keyed by `agentId`.

```csharp
using Vais.Agents.Hosting.Orleans;

// Silo side:
siloBuilder.ConfigureAgentGrains((sp, grainKey) =>
{
    // Build the StatefulAgentOptions for each agent grain here:
    return new StatefulAgentOptions { /* … */ };
});

// Client side:
services.AddOrleansAgentRuntime();
// Resolve IAgentRuntime; drive turns from your service code.
IAgentRuntime runtime = /* injected */;
IAgentSession session = runtime.GetSession("support-agent", "conv-42");
var agent = new StatefulAiAgent(provider, new StatefulAgentOptions { Session = session });
```

The **LLM turn loop stays client-side** in `StatefulAiAgent`. The silo is a state + streams backbone, not a compute host. This matches Bedrock AgentCore / OpenAI Assistants semantics.

### Why client-side loop

Keeping `StatefulAiAgent` on the client means each caller pays its own LLM latency (good for isolation), grains stay cheap + deterministic (easy to serialise across clusters), and consumers can mix live streaming with silo-persisted state without serialising `IAsyncEnumerable` through Orleans RPC.

### Serialisation surrogates

Abstractions stays Orleans-free. The Orleans hosting package ships `[RegisterConverter]` surrogates for `ChatTurn`, `AgentContext`, and the closed `AgentEvent` hierarchy. One converter per subclass (Orleans dispatches by exact runtime type, not polymorphic-by-base) — handled in-package so consumers don't see the machinery.

## Redis — clustering + grain storage + streams

`Vais.Agents.Persistence.Redis`:

```csharp
using Vais.Agents.Persistence.Redis;

siloBuilder.UseAgenticRedisClustering(connectionString);
siloBuilder.AddAgenticRedisGrainStorage(connectionString);
siloBuilder.UseAgenticRedisStreaming(connectionString);   // new in v0.3 (post 10.x dep upgrade)

clientBuilder.UseAgenticRedisClustering(connectionString);
clientBuilder.UseAgenticRedisStreaming(connectionString);
```

Each wrapper is a thin DI helper that pre-sets the conventions (e.g. `AddAgenticRedisGrainStorage` registers the provider under `AiAgentGrain.StorageName` = `"vais.agents"` so consumers don't need to re-type the storage name).

**Known constraint:** `Microsoft.Orleans.Streaming.Redis` is published as `10.1.0-alpha.1` only (no stable 10.x yet). Flagged in the package README. Revisit when a stable build ships.

## Postgres — clustering + grain storage

`Vais.Agents.Persistence.Postgres`:

```csharp
using Vais.Agents.Persistence.Postgres;

siloBuilder.UseAgenticPostgresClustering(connectionString);
siloBuilder.AddAgenticPostgresGrainStorage(connectionString);

clientBuilder.UseAgenticPostgresClustering(connectionString);
```

Both wrappers pre-set `Invariant = "Npgsql"` (exposed as `AgenticPostgresPersistenceExtensions.NpgsqlInvariant` for consumers who need to build custom ADO.NET options).

**Schema provisioning is the consumer's job.** Orleans' ADO.NET provider expects specific tables (`OrleansMembershipTable`, `OrleansStorage`, etc.); the SQL scripts live in the Orleans repo, not in our package. The [Add Postgres persistence guide](../guides/add-postgres-persistence.md) walks through fetching and applying them.

## Orleans stream provider name

The `OrleansAgentEventBus` always uses a stream provider named `"vais.agents.events"` (the default; configurable). Wire it on both silo and client:

```csharp
// Memory streams (dev/tests):
siloBuilder.AddMemoryStreams("vais.agents.events");
siloBuilder.AddMemoryGrainStorage("PubSubStore");  // required by memory streams
clientBuilder.AddMemoryStreams("vais.agents.events");

// Redis streams (Redis-backed; see constraint above):
siloBuilder.UseAgenticRedisStreaming(connectionString);
clientBuilder.UseAgenticRedisStreaming(connectionString);

// Azure Event Hubs:
siloBuilder.AddEventHubStreams("vais.agents.events", configure);
```

Any Orleans-compatible stream provider works — the bus doesn't care about the transport.

## VectorData-backed RAG <a name="rag"></a>

`Vais.Agents.Persistence.VectorData` provides:

- **`VectorStoreKnowledgeRetriever<TKey, TRecord>`** — implements `IKnowledgeRetriever` over any `Microsoft.Extensions.VectorData` collection. Takes an `IEmbeddingGenerator<string, Embedding<float>>` for query embedding + a projection from `TRecord` → `KnowledgeChunk`.
- **`KnowledgeRetrievalContextProvider`** — an `IContextProvider` that extracts the latest user turn, queries the retriever, and injects the top-K chunks as a `retrieval.docs` system section. Uses a configurable template.

```csharp
using Vais.Agents.Persistence.VectorData;

var retriever = new VectorStoreKnowledgeRetriever<string, DocRecord>(
    collection: vectorStoreCollection,
    embedder: embeddingGenerator,
    project: r => new KnowledgeChunk(r.Text, Id: r.Id));

var agent = new StatefulAiAgent(
    provider,
    new StatefulAgentOptions
    {
        ContextProviders = new IContextProvider[]
        {
            new KnowledgeRetrievalContextProvider(retriever, new KnowledgeRetrievalOptions { TopK = 5 }),
        },
    });
```

**Not included:** document ingestion. Loading docs into the vector store is a consumer concern — MEAI's `IEmbeddingGenerator` + `VectorStoreCollection.UpsertAsync` is the idiomatic path. We don't ship ingestion helpers.

### Legacy filter (obsolete)

`KnowledgeRetrievalFilter : IAgentFilter` shipped in v0.3 before the context-provider pillar existed. It's `[Obsolete(DiagnosticId="VAIS0001")]` since v0.4 but still ships for backward compatibility (no firm removal version set). New code uses `KnowledgeRetrievalContextProvider`.

## Extension points

- **Any Orleans clustering / grain-storage / streaming provider** works — we ship Redis + Postgres helpers but the contract is "any Orleans-compatible provider". Roll your own for Cosmos, DynamoDB, etc.
- **Any `Microsoft.Extensions.VectorData` collection** — Qdrant, pgvector, Azure AI Search, Weaviate, Redis, InMemory. `Vais.Agents.Persistence.VectorData` is agnostic.
- **`IMemoryStore`** — unrelated to Orleans grain storage; a separate persistence layer for scoped KV recall. See [session + memory](session.md).

## Observability

- Grain activations / deactivations surface through Orleans' own telemetry.
- `OrleansAgentEventBus.PublishAsync` failures are swallowed + logged at the agent level (same discipline as `IUsageSink`).
- No Orleans-specific activity tags are added by the Vais.Agents layer; rely on Orleans' native `Microsoft.Orleans` source if you need grain-call spans.

## Limitations / known gaps

- **`Microsoft.Orleans.Streaming.Redis 10.1.0-alpha.1`** is the only published Redis-streaming build at Orleans 10.1. Use at your own risk; revisit when upstream ships stable.
- **No `IChatHistoryStore`** — an abstract "save chat history to X" interface was considered but deferred; `IAgentSession` already solves the load-on-construct case, and only one real implementation would exist right now (Postgres). Revisit when a second provider case shows up.
- **No migration helpers for Orleans schema.** Postgres schema provisioning is manual; the SQL scripts live in the Orleans repo.
- **VectorData pinned at 10.1.0** until SK's InMemory connector (used in tests) catches up; consumers using 10.5+ in their own stack override the pin locally.

## See also

- [Architecture](architecture.md)
- [Session + memory](session.md)
- [Context](context.md) — `KnowledgeRetrievalContextProvider` merges via the context chain.
- [Add Redis persistence guide](../guides/add-redis-persistence.md)
- [Add Postgres persistence guide](../guides/add-postgres-persistence.md)
- [Wire RAG via VectorData guide](../guides/wire-rag-via-vectordata.md)
- [Run on Orleans locally guide](../guides/run-on-orleans-locally.md)
