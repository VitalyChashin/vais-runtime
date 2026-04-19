# Guide: wire RAG via VectorData

Augment each turn's system prompt with retrieved chunks from a vector store. Uses `Vais.Agents.Persistence.VectorData` over any `Microsoft.Extensions.VectorData` collection — Qdrant, pgvector, Azure AI Search, Weaviate, InMemory, etc.

## Packages

```xml
<PackageReference Include="Vais.Agents.Persistence.VectorData" Version="0.4.0-preview" />
<PackageReference Include="Microsoft.Extensions.VectorData.Abstractions" Version="10.1.0" />
<!-- Plus your vector-store connector, e.g.: -->
<PackageReference Include="Microsoft.SemanticKernel.Connectors.InMemory" Version="1.74.0-preview" />
```

## Define your record

A vector-store record ties `Id`, `Text`, and the embedding vector together:

```csharp
using Microsoft.Extensions.VectorData;

sealed class DocRecord
{
    [VectorStoreKey]     public required string Id { get; init; }
    [VectorStoreData]    public required string Text { get; init; }
    [VectorStoreVector(1536)] public required ReadOnlyMemory<float> Embedding { get; init; }
}
```

(Dimensions = your embedder's output size. `1536` for OpenAI's `text-embedding-3-small`.)

## Ingest documents

Loading docs into the store is a consumer concern — Vais.Agents doesn't ship ingestion helpers. The idiomatic MEAI path:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel.Connectors.InMemory;

var store = new InMemoryVectorStore();
var collection = store.GetCollection<string, DocRecord>("docs");
await collection.CreateCollectionIfNotExistsAsync();

var embedder = /* your IEmbeddingGenerator<string, Embedding<float>> */;

foreach (var (id, text) in documents)
{
    var vec = await embedder.GenerateAsync(text);
    await collection.UpsertAsync(new DocRecord { Id = id, Text = text, Embedding = vec.Vector });
}
```

## Wire the retriever + context provider

```csharp
using Vais.Agents;
using Vais.Agents.Persistence.VectorData;
using Vais.Agents.Core;

var retriever = new VectorStoreKnowledgeRetriever<string, DocRecord>(
    collection: collection,
    embedder: embedder,
    project: r => new KnowledgeChunk(r.Text, Id: r.Id));

var provider = new KnowledgeRetrievalContextProvider(
    retriever,
    new KnowledgeRetrievalOptions
    {
        TopK = 5,
        Template = "Relevant context:\n{chunks}",   // {chunks} expands to "-chunk1-\n---\n-chunk2-\n---\n..."
        ChunkSeparator = "\n---\n",
    });

var agent = new StatefulAiAgent(
    completionProvider,
    new StatefulAgentOptions
    {
        ContextProviders = new IContextProvider[] { provider },
    });

Console.WriteLine(await agent.AskAsync("What's our return policy?"));
```

## What the provider does per turn

1. Finds the latest `ChatRole.User` turn in the request's history.
2. Calls `retriever.RetrieveAsync(query: userText, topK)` — embeds + searches.
3. Formats the top-K chunks via the template.
4. Returns a `ContextContribution { SystemPromptAddendum = formattedChunks }`.

The agent's merge rules concatenate the addendum onto the base system prompt with `\n\n`. Canonical layered shape: `{composed-base}\n\nRelevant context:\n{chunks}`.

Zero-chunks results (or no-user-turn in history) return `ContextContribution.Empty` — no-op, no addendum.

## Custom `IKnowledgeRetriever`

If `VectorStoreKnowledgeRetriever<TKey, TRecord>` doesn't fit (hybrid search, multi-store federation, rerankers), implement the interface:

```csharp
public interface IKnowledgeRetriever
{
    Task<IReadOnlyList<KnowledgeChunk>> RetrieveAsync(string query, int topK = 5, CancellationToken cancellationToken = default);
}
```

Caching is your job — the retriever is called once per turn. Wrap with your own cache decorator if retrieval is expensive.

## Known pins

- `Microsoft.Extensions.VectorData.Abstractions` is pinned at **10.1.0** in this repo because `Microsoft.SemanticKernel.Connectors.InMemory 1.74.0-preview` (used in our tests) was built against 10.1 and references `VectorSearchFilter` — removed in 10.5. Your own app can override the pin once SK's InMemory catches up; the retriever doesn't care about the specific patch version.
- The `[VectorStoreKey]` / `[VectorStoreData]` / `[VectorStoreVector]` attributes (no `Record` prefix) are VectorData 9.7+ names. Earlier MEAI VectorData had `[VectorStoreRecord*]` — don't mix.

## Legacy filter — obsolete

Vais.Agents 0.3 shipped `KnowledgeRetrievalFilter : IAgentFilter` — same retrieval + template pattern, wired into the filter pipeline. v0.4 replaced it with `KnowledgeRetrievalContextProvider` (context-chain integration is cleaner). The filter is still present but marked `[Obsolete(DiagnosticId="VAIS0001")]` — removal scheduled for v0.5.

```csharp
// If you must use the legacy filter (for a one-release overlap):
#pragma warning disable VAIS0001
var legacy = new KnowledgeRetrievalFilter(retriever, options);
#pragma warning restore VAIS0001
```

## Things that catch people

- **Embedding-model mismatch.** Ingest + query embeddings must come from the same model. Different models = different vector geometries = worthless search.
- **TopK tuning.** Too few → misses context; too many → prompt bloat + latency. 3-7 is typical for conversational agents.
- **No automatic re-ingestion.** If your docs change, the retriever reads whatever's in the collection right now. Ingestion frequency is your scheduler's job.
- **`VectorSearchResult.Score` is `double?`** but `KnowledgeChunk.Score` is `float?` — the built-in retriever casts explicitly. If you implement your own, match the contract.

## See also

- [Context concept](../concepts/context.md)
- [Persistence concept](../concepts/persistence.md)
- [Prompt concept](../concepts/prompt.md) — composer + addendum concatenation.
- Sample: `samples/VectorDataRag/` (per samples plan)
