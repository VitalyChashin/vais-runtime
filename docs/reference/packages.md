# Reference: packages

All 13 packages under the `Vais.Agents.*` prefix. Target framework: `net9.0`. Version: `0.4.0-preview` (not yet published to nuget.org).

## Contracts

| Package | Purpose | Install when… |
|---|---|---|
| `Vais.Agents.Abstractions` | Neutral contracts + value records. No SK / MAF / Orleans deps. | Authoring a custom adapter or host. Otherwise pulled transitively. |

## Core

| Package | Purpose | Install when… |
|---|---|---|
| `Vais.Agents.Core` | Default `StatefulAiAgent` + execution loop + in-process defaults + diagnostics constants. | Any scenario that builds or runs an agent. |

## Adapters

| Package | Purpose | Install when… |
|---|---|---|
| `Vais.Agents.Ai.SemanticKernel` | `SkCompletionProvider` over SK's `IChatCompletionService`. | Your app has a `Kernel` and you want agents on SK. |
| `Vais.Agents.Ai.MicrosoftAgentFramework` | `MafCompletionProvider` over MAF's `ChatClientAgent` + MEAI's `IChatClient`. | Your app uses MEAI / MAF and you want agents on that stack. |

## Hosting

| Package | Purpose | Install when… |
|---|---|---|
| `Vais.Agents.Hosting.InMemory` | `InMemoryAgentRuntime` + `InMemoryAgentEventBus`. One process, no cluster. | Dev, CLI tools, tests. |
| `Vais.Agents.Hosting.Orleans` | `OrleansAgentRuntime` + `AiAgentGrain` + `IAgentSessionGrain` + `IAgentConfigGrain` + `OrleansAgentEventBus`. | Multi-process or clustered deployments needing durable state. |

## Persistence

| Package | Purpose | Install when… |
|---|---|---|
| `Vais.Agents.Persistence.Redis` | `UseAgenticRedisClustering` + `AddAgenticRedisGrainStorage` + `UseAgenticRedisStreaming`. | Running Orleans with Redis for membership / grain storage / streams. |
| `Vais.Agents.Persistence.Postgres` | `UseAgenticPostgresClustering` + `AddAgenticPostgresGrainStorage`. No streams provider (alpha-only upstream). | Running Orleans with Postgres for membership / grain storage. |
| `Vais.Agents.Persistence.VectorData` | `VectorStoreKnowledgeRetriever<TKey, TRecord>` + `KnowledgeRetrievalContextProvider` + `[Obsolete]` `KnowledgeRetrievalFilter`. | RAG — augmenting prompts with retrieved chunks from any MEAI VectorData collection. |

## Observability

| Package | Purpose | Install when… |
|---|---|---|
| `Vais.Agents.Observability.OpenTelemetry` | `OpenTelemetryUsageSink` + `AddAgenticInstrumentation` extensions for Tracer / Meter providers. | Exporting traces + metrics to any OTel collector (Jaeger, Tempo, Datadog, Grafana). |
| `Vais.Agents.Observability.Langfuse` | `LangfuseEnrichmentFilter` — adds `langfuse.*` tags to active Activity. | You route OTLP to Langfuse and want first-class UI recognition. |

## Protocols (interop)

| Package | Purpose | Install when… |
|---|---|---|
| `Vais.Agents.Protocols.Mcp` | `McpToolSource : IToolSource`. Outbound: pull tools from an MCP server into the local registry. | Your agent needs to use tools exposed by an MCP-speaking server. |
| `Vais.Agents.Protocols.A2A` | `A2ARemoteAgentTool : ITool`. Outbound: wrap a remote A2A agent as a local tool. | Your agent needs to delegate subtasks to a peer A2A agent. |

## Typical scenario bundles

- **Single-process agent on SK** — `Abstractions` + `Core` + `Ai.SemanticKernel`.
- **Single-process agent on MAF** — `Abstractions` + `Core` + `Ai.MicrosoftAgentFramework`.
- **Clustered agent on Orleans + Redis** — above + `Hosting.Orleans` + `Persistence.Redis`.
- **Clustered agent on Orleans + Postgres** — above + `Hosting.Orleans` + `Persistence.Postgres`.
- **Observability stack** — any of the above + `Observability.OpenTelemetry` (+ optionally `Observability.Langfuse`).
- **RAG-augmented** — any of the above + `Persistence.VectorData`.
- **MCP + A2A interop** — any of the above + `Protocols.Mcp` + `Protocols.A2A`.
- **Everything** — 13 packages; see `artifacts/smoketest/` for what the full stack looks like.

## Version pins (in `Directory.Packages.props`)

See [installation](../getting-started/installation.md) for the full pin list.

## See also

- [Architecture concept](../concepts/architecture.md) — dependency layering diagram.
- [Installation](../getting-started/installation.md)
