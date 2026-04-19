# VectorDataRag

End-to-end RAG via `Microsoft.Extensions.VectorData` over SK's `InMemory` connector. Ingests three docs, wires `VectorStoreKnowledgeRetriever` + `KnowledgeRetrievalContextProvider`, drives a turn; prints the augmented system prompt.

Uses a SHA256-backed fake embedder for determinism — exact-string matches score 1.0. Swap for a real `IEmbeddingGenerator` in production.

**Concepts:** [context](../../docs/concepts/context.md), [persistence (RAG)](../../docs/concepts/persistence.md#rag).
**Packages:** `Vais.Agents.Abstractions`, `Vais.Agents.Core`, `Vais.Agents.Persistence.VectorData`, `Microsoft.SemanticKernel.Connectors.InMemory`.
**Needs API key:** no.

```bash
dotnet run --project samples/VectorDataRag
```
