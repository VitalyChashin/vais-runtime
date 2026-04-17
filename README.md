# Vais2.Agents

> **Status: Phase 1 — pre-alpha, pre-release.** API unstable. NuGet not yet published.
> Name is a working placeholder; a trademark / NuGet-existing-package clearance pass is pending before any public push.

Durable, multi-tenant agent hosting for .NET — with pluggable AI stacks (**Microsoft Agent Framework** and **Semantic Kernel**) sitting behind a single neutral abstraction.

Born from the [VAIS2 Platform](../..) internal `Vais2Agents.*` projects, extracted under Apache 2.0 so the framework can live without the surrounding product.

## Why

Today, picking an agent library also picks your LLM stack. We want the opposite: one library whose agents retain durable identity and state, with the completion stack swappable between SK and MAF (and more, later) via DI.

## Design principles

1. **Stack-neutral contracts.** `Vais2.Agents.Abstractions` references no SK, no MAF, no Orleans. Consumers depend on it without pulling either stack.
2. **Native code paths, not shims.** Each adapter exercises its own stack's real machinery — SK's `IChatCompletionService`, MAF's `ChatClientAgent` — rather than reducing both to a common `IChatClient` pass-through.
3. **History and identity live in the core.** The library owns conversation state; it does not delegate to a stack's per-invocation session unless explicitly asked.
4. **Apache 2.0 across the stack** — library and (future) cloud runtime both.

## Repository layout

```
src/
  Vais2.Agents.Abstractions/          Neutral contracts (no SK/MAF refs)
  Vais2.Agents.Core/                  Default stateful agent + state primitives
  Vais2.Agents.Ai.SemanticKernel/     SK adapter
  Vais2.Agents.Ai.MicrosoftAgentFramework/  MAF adapter
tests/
  Vais2.Agents.Core.Tests/            Unit tests (no network)
samples/
  HelloAgent/                         Runs the same agent twice — SK, then MAF
```

## Quick start

```bash
dotnet build
dotnet test
OPENAI_API_KEY=... dotnet run --project samples/HelloAgent
```

## Architecture decisions

Short records of decisions that shape the public surface live in [`docs/adr/`](docs/adr/). The current set:

- [ADR 0001 — Keyed `IChatClient` DI convention](docs/adr/0001-keyed-ichatclient-di-convention.md) — the colon-delimited string-key shape (`openai:gpt-4o:primary`) used when consumers register multiple providers side-by-side.

## Roadmap (high level)

- **M1 (current):** Core contracts + SK and MAF adapters + minimal sample + Core unit tests.
- **M2:** In-memory agent runtime, parity tests, observability (OTel + Langfuse enricher).
- **M3:** Orleans host, Redis and Postgres persistence, `VectorData`-based RAG package.
- **M4:** Cloud runtime (A2A protocol-native, declarative agents, Helm chart).

Full roadmap and design decisions: `../../plans/actor-agents-oss-extraction-research.md` in the parent VAIS2 repo (will move here when the OSS repo goes public).

## Building

- .NET 9 SDK (or newer).
- Pinned deps resolve to SK 1.62, MAF `1.0.0-preview.251009.1`, MEAI 9.10.
- `NU1608` (SK vs MEAI.OpenAI disagreement on `OpenAI` SDK version) is suppressed globally pending SK bumping its floor.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). The library is under active design — breaking changes to public API are expected until the first tagged alpha.

## License

[Apache 2.0](LICENSE).
