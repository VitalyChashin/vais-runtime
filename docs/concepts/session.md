# Session + memory

An agent's conversation identity is the **session**. Its cross-run recall is **memory**. They're separate pillars: session is authoritative for "what was just said"; memory is optional durable recall scoped per session, per agent, or per tenant.

## Why two concepts

In earlier designs we collapsed both into an `AgentId` + a mutable `History` list. That made multi-session-per-agent impossible, made branch/fork meaningless, and made cross-run recall either an agent's problem or the host's — with no seam. Splitting gives each its own contract:

- `IAgentSession` — the conversation the agent is inside right now. Owns `History`, addresses a `(AgentId, SessionId)` pair, survives activation boundaries.
- `IMemoryStore` — a key-value store scoped by `MemoryScope`, with optional durability tag and substring search.

`StatefulAiAgent` uses the session authoritatively (history is the session). MemoryStore is exposed through `StatefulAgentOptions` but *not* auto-consumed by the execution loop — you wire it into a context provider or a tool to surface it into a turn.

## Core types

```csharp
namespace Vais.Agents;

public interface IAgentSession
{
    string AgentId { get; }
    string? SessionId { get; }
    IReadOnlyList<ChatTurn> History { get; }
    Task AppendAsync(ChatTurn turn, CancellationToken cancellationToken = default);
    Task ResetAsync(CancellationToken cancellationToken = default);
}

public interface IMemoryStore
{
    Task WriteAsync(MemoryScope scope, string key, MemoryItem item, CancellationToken cancellationToken = default);
    Task<MemoryItem?> ReadAsync(MemoryScope scope, string key, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MemorySearchResult>> SearchAsync(MemoryScope scope, string query, int topK = 10, CancellationToken cancellationToken = default);
    Task DeleteAsync(MemoryScope scope, string key, CancellationToken cancellationToken = default);
}

public sealed record MemoryScope(string? AgentId = null, string? SessionId = null, string? TenantId = null, MemoryDurability Durability = MemoryDurability.ShortTerm);
public sealed record MemoryItem(string Content, IReadOnlyDictionary<string, string>? Metadata = null);
public sealed record MemorySearchResult(string Key, MemoryItem Item, float? Score = null);
public enum MemoryDurability { ShortTerm, LongTerm, Working }
```

## Default implementations (in Core)

- **`InMemoryAgentSession(agentId, sessionId?, initialHistory?)`** — `List<ChatTurn>`-backed. The default when you don't pass `StatefulAgentOptions.Session`.
- **`InMemoryMemoryStore`** — scope-partitioned via record equality, case-insensitive substring search.
- **`NullMemoryStore.Instance`** — does nothing.
- **`NoopHistoryReducer.Instance`** — the default `IHistoryReducer`, passes history through unchanged.

## Wiring

Default (the agent creates its own session):

```csharp
var agent = new StatefulAiAgent(
    provider,
    new StatefulAgentOptions { SystemPrompt = "Be concise." });
// Uses a fresh InMemoryAgentSession under the hood.
```

Bring your own session (e.g. an Orleans-backed one):

```csharp
using Vais.Agents.Hosting.Orleans;

IAgentRuntime runtime = /* OrleansAgentRuntime injected */;
IAgentSession session = runtime.GetSession("customer-support", "conv-42");

var agent = new StatefulAiAgent(
    provider,
    new StatefulAgentOptions { Session = session });
```

Wire a memory store (consumed by your own code — context providers, tools, handlers):

```csharp
var memory = new InMemoryMemoryStore();

var agent = new StatefulAiAgent(
    provider,
    new StatefulAgentOptions { MemoryStore = memory });

// Write from a tool, read from a context provider, etc.
await memory.WriteAsync(
    new MemoryScope(SessionId: session.SessionId, Durability: MemoryDurability.LongTerm),
    key: "user.preferred-language",
    new MemoryItem("fr-FR"));
```

## Session vs working history

Inside `AskAsync` / `StreamAsync` the agent maintains a **working history** for the run — it starts as a snapshot of `Session.History` (which includes the just-appended user turn) and grows with assistant-with-tool-calls + tool-result turns between the outer loop's iterations. The session itself is mutated only twice per run:

1. User turn appended at run entry.
2. Final assistant turn appended at run exit.

Result: `session.History` stays clean — user / assistant / user / assistant — even when the agent made multiple tool-dispatching rounds in between. This matches the [execution loop](execution-loop.md) diagram.

## Orleans hosting — grain per `(agentId, sessionId)`

`OrleansAgentRuntime.GetSession(agentId, sessionId)` returns an `OrleansAgentSession` backed by an `IAgentSessionGrain`. The grain key is `{agentId}/{sessionId}` via `OrleansSessionGrainKey.Encode` (the helper validates both ids — `/` is rejected in either). Per-agent shared config lives in `IAgentConfigGrain`, keyed by `agentId` alone.

The session grain is a **pure state container** — it does NOT run the LLM turn loop. That stays client-side in `StatefulAiAgent`. This matches the Bedrock AgentCore / OpenAI Assistants "session = state, agent = execution" split. It also means the Orleans grain is small, fast, and easy to serialise across clusters; the expensive completion calls happen on whichever client owns the turn.

## Extension points

- **Bring your own `IAgentSession`** — any durable store works. Append has to be async; history read is sync + atomic to the last append.
- **Bring your own `IMemoryStore`** — implement the four methods. `MemoryScope` is `record`-compared, so `SearchAsync` implementations typically partition by scope first, then search.
- **Bring your own `IHistoryReducer`** — the agent calls `_historyReducer.ReduceAsync(workingHistory, ct)` before each turn's `CompletionRequest`. Use this to trim long histories, summarise, or enforce a sliding window. Default `NoopHistoryReducer.Instance` passes through.

## Observability

- `StatefulAiAgent` tags the per-turn Activity with `vais.agent.name` (from `AgentContext.AgentName` or `options.AgentName`).
- `AgentEvent.Context.UserId` / `TenantId` / `CorrelationId` flow onto events.
- No session-level events ship in v0.4 — consumers who want "session started / ended" can emit their own via a custom `IAgentFilter`.

## Limitations / known gaps

- **Session branching / forking** is not modelled explicitly — consumers create a new `SessionId` and seed it with `StatefulAgentOptions.InitialHistory` (when no session is injected) to fork.
- **Cross-session memory queries** are scope-based; there's no built-in "search across all my sessions" — pass a narrower scope, or add a bespoke search tool.
- **Memory eviction / TTL** is a consumer concern — `MemoryDurability` is a tag, not an enforcement.
- **`InMemoryAgentSession` is not thread-safe** for concurrent `AppendAsync`s on one instance. `StatefulAiAgent` serialises per instance; external callers must too.

## See also

- [Architecture](architecture.md) — how sessions fit the package graph.
- [Execution loop](execution-loop.md) — session vs working history split.
- [Persistence](persistence.md) — Orleans + Redis + Postgres backings.
