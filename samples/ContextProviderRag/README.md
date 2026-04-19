# ContextProviderRag

Wires [`KnowledgeRetrievalContextProvider`](../../docs/concepts/context.md) with a mock `IKnowledgeRetriever`. Shows the `SystemPromptAddendum` concatenation of retrieved chunks into the request's system prompt.

**Concepts:** [context](../../docs/concepts/context.md), [persistence (RAG)](../../docs/concepts/persistence.md#rag).
**Packages:** `Vais.Agents.Abstractions`, `Vais.Agents.Core`, `Vais.Agents.Persistence.VectorData`.
**Needs API key:** no.

```bash
dotnet run --project samples/ContextProviderRag
```
