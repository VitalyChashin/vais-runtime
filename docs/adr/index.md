# Architecture Decision Records

Short records of decisions that shape the public surface. Each ADR captures the *why*, not the *what* — the code is authoritative for what; the ADR explains why it's that way.

## Index

| # | Title | Status | Date |
|---|---|---|---|
| [0001](0001-keyed-ichatclient-di-convention.md) | Keyed `IChatClient` DI convention | Accepted | 2026-04-17 |
| [0002](0002-otel-genai-conventions.md) | OpenTelemetry GenAI semantic conventions | Accepted | 2026-04-17 |
| [0003](0003-streaming-filter-contract.md) | Streaming filter contract — one DIM method, three override points | Accepted | 2026-04-20 |
| [0004](0004-sse-event-taxonomy-on-wire.md) | SSE streams carry the full `AgentEvent` taxonomy | Accepted | 2026-04-20 |
| [0005](0005-container-plugin-protocol.md) | Container plugin protocol design | Accepted | 2026-05-08 |

## Adding a new ADR

1. Copy the format of `0001-keyed-ichatclient-di-convention.md`.
2. Sections: **Status**, **Context bounded by**, **Replaces** (or **Supersedes**), then **Context**, **Decision** (numbered), **Why not…**, **Consequences**, **Follow-ups**.
3. Bump the number, link from this index.
4. Status starts as *Proposed*; move to *Accepted* when the PR lands with the code. *Superseded* when another ADR overrides this one; link forward.

## Scope

ADRs here cover **public-surface** decisions — things consumers see or depend on. Internal implementation decisions (e.g. "how we serialise `AgentEventSurrogate`") live in code comments, not ADRs.

## See also

- [Architecture concept](../concepts/architecture.md) — the decisions realised in code.
