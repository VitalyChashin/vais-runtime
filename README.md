# Vais.Agents

<!-- TODO: replace every `<org>/<repo>` in this file once the GitHub org / repo names land. -->

[![Build](https://github.com/<org>/<repo>/actions/workflows/ci.yml/badge.svg)](https://github.com/<org>/<repo>/actions/workflows/ci.yml)
[![License: Apache 2.0](https://img.shields.io/badge/license-Apache_2.0-blue.svg)](LICENSE)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4.svg)](https://dotnet.microsoft.com/download)
[![NuGet status](https://img.shields.io/badge/NuGet-pre--alpha-orange.svg)](#)

> **Status: Phase 1 — pre-alpha, pre-release.** API unstable. NuGet not yet published.
> A trademark / NuGet-existing-package clearance pass is pending before any public push of the `Vais.Agents.*` package ids.

Stack-neutral agent library for .NET — durable multi-tenant hosting with pluggable AI backends. Pick **Microsoft Agent Framework** or **Semantic Kernel** via DI; swap without rewriting your agent.

## Why Vais.Agents?

Most .NET agent libraries lock you to one AI stack. Start on Semantic Kernel; switching to Microsoft Agent Framework means rewriting. Ship a multi-tenant runtime; build durability yourself. Vais.Agents addresses both: a stack-neutral library that treats SK and MAF as swappable backends, paired with an Orleans-based virtual-actor host that gives every agent grain-level durability and per-session checkpointing without bespoke plumbing.

Stack-neutral does not mean lowest-common-denominator. Each adapter exercises its stack's native machinery — SK's `IChatCompletionService`, MAF's `ChatClientAgent` — rather than collapsing both to a shared `IChatClient` shim. The same `StatefulAiAgent` class works against either; the choice is a single DI registration.

## What you get

| Capability | Packages |
|---|---|
| Stack-neutral contracts — `IAiAgent`, `IAgentSession`, `ICompletionProvider`, `ITool`, guardrails, events | `Vais.Agents.Abstractions` |
| Default stateful agent with tool-call outer loop, budget, interrupts, streaming | `Vais.Agents.Core` |
| Adapters — exercise each stack's real machinery, not a `IChatClient` pass-through | `Vais.Agents.Ai.SemanticKernel`, `Vais.Agents.Ai.MicrosoftAgentFramework` |
| In-memory host for dev / tests | `Vais.Agents.Hosting.InMemory` |
| Orleans virtual-actor host — grain-per-(agent, session), durable state, event streams | `Vais.Agents.Hosting.Orleans` |
| Persistence — Redis + Postgres for Orleans clustering / grain storage / streams | `Vais.Agents.Persistence.Redis`, `Vais.Agents.Persistence.Postgres` |
| Observability — OTel GenAI semantic conventions + Langfuse enrichment | `Vais.Agents.Observability.OpenTelemetry`, `Vais.Agents.Observability.Langfuse` |
| RAG via `Microsoft.Extensions.VectorData` | `Vais.Agents.Persistence.VectorData` |
| Interop — MCP tool-source adapter, A2A remote-agent-as-tool adapter | `Vais.Agents.Protocols.Mcp`, `Vais.Agents.Protocols.A2A` |

## 30-second hello

```csharp
using Microsoft.SemanticKernel;
using Vais.Agents;
using Vais.Agents.Ai.SemanticKernel;
using Vais.Agents.Core;

var kernel = Kernel.CreateBuilder()
    .AddOpenAIChatCompletion("gpt-4o-mini", Environment.GetEnvironmentVariable("OPENAI_API_KEY")!)
    .Build();

var agent = new StatefulAiAgent(
    new SkCompletionProvider(kernel),
    new StatefulAgentOptions { SystemPrompt = "Be concise." });

Console.WriteLine(await agent.AskAsync("What is the capital of France?"));
Console.WriteLine(await agent.AskAsync("And its population?"));  // History carries.
```

Swap the adapter to MAF — one `using` change, same agent class:

```csharp
using Microsoft.Extensions.AI;
using Vais.Agents.Ai.MicrosoftAgentFramework;

IChatClient client = /* your MEAI chat client */;
var agent = new StatefulAiAgent(new MafCompletionProvider(client), options);
```

## Documentation

Full docs live under **[`docs/`](docs/index.md)** — getting-started walkthrough, per-pillar concepts, how-to guides, API reference, ADR index. Start at [`docs/index.md`](docs/index.md).

## Design principles

1. **Stack-neutral contracts.** `Vais.Agents.Abstractions` references no SK, no MAF, no Orleans. Consumers depend on it without pulling either stack.
2. **Native code paths, not shims.** Each adapter exercises its own stack's real machinery — SK's `IChatCompletionService`, MAF's `ChatClientAgent` — rather than reducing both to a common `IChatClient` pass-through.
3. **History and identity live in the core.** The library owns conversation state; it does not delegate to a stack's per-invocation session unless explicitly asked.
4. **Apache 2.0 across the stack** — library and (future) cloud runtime both.

## Building

Requires **.NET 9 SDK**.

Run any sample end-to-end from a fresh clone:

```bash
git clone https://github.com/<org>/<repo>.git
cd <repo>
dotnet pack Vais.Agents.sln --configuration Release --output artifacts/packages
OPENAI_API_KEY=sk-... dotnet run --project samples/HelloAgent
```

The `dotnet pack` step populates the local NuGet feed under `artifacts/packages/` that samples consume from via `NuGet.config`. Most samples use scripted fake completion providers and don't need `OPENAI_API_KEY`; the `HelloAgent` sample calls a real OpenAI endpoint so the key is required for that one.

Standalone build + test:

```bash
dotnet build Vais.Agents.sln
dotnet test  Vais.Agents.sln
```

Pinned deps resolve to SK 1.74, MAF 1.1, MEAI 10.5, Orleans 10.1, OpenAI 2.10. `Microsoft.Extensions.VectorData.Abstractions` is held at 10.1.0 because the SK 1.74 InMemory preview connector (used in tests) was built against that surface; lift in lockstep with SK.Connectors.InMemory.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). The library is under active design — breaking changes to public API are expected until the first tagged alpha.

## License

[Apache 2.0](LICENSE).
