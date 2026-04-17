# ADR 0001: Keyed `IChatClient` DI convention

- **Status:** Accepted — 2026-04-17 (M2a)
- **Context bounded by:** Phase 1 of the Vais2.Agents OSS extraction (`plans/actor-agents-oss-extraction-research.md` in the parent VAIS2 repo).
- **Replaces:** `SemanticKernelPooling` (retired, per decision §4.8 of the research doc).

## Context

Consumers commonly want to register several `IChatClient` / `ICompletionProvider` instances side by side — for model fan-out ("route cheap prompts to `gpt-4o-mini`, expensive ones to `gpt-4o`"), provider redundancy ("primary OpenAI key, fallback to a secondary"), and A/B testing. The retired `SemanticKernelPooling` offered this through a bespoke pool-by-scope API; `Microsoft.Extensions.DependencyInjection` offers it natively through keyed services (`.AddKeyedSingleton<T>(key, ...)`).

The shape of those keys is a public contract the moment anyone registers one, so we pick it once and document it here rather than inventing per-feature.

## Decision

**Use plain `string` keys with a colon-delimited hierarchy**, namespaced from coarse to specific:

```
<provider>:<model>[:<purpose>[:<variant>]]
```

Concrete examples:

| Key | Meaning |
|---|---|
| `"openai:gpt-4o-mini"` | The default small-model OpenAI client. |
| `"openai:gpt-4o:primary"` | Primary OpenAI key for the large model. |
| `"openai:gpt-4o:secondary"` | Fallback key for the same model. |
| `"azure:gpt-4o:eastus"` | Region-specific Azure OpenAI deployment. |
| `"anthropic:claude-sonnet-4-6"` | Anthropic Sonnet. |
| `"fake:test"` | A test double. |

Rules:

1. **Lowercase and dash-separated segments.** `gpt-4o-mini`, not `GPT_4oMini`.
2. **Three segments is the sweet spot**: provider, model, purpose. Add a fourth only for a real reason (region, tenant, experiment arm).
3. **The unkeyed default** (no key) is the single "go-to" client for that application — libraries that want "just a chat client, don't make me choose" resolve `IChatClient`, not a keyed one.
4. **Library code** (everything under `src/`) does **not** register clients, keyed or otherwise. That is the consumer's job. Libraries consume `ICompletionProvider` and may depend on `[FromKeyedServices("...")]` only when a feature fundamentally requires multiple concurrent providers (e.g. a future multi-agent orchestrator).
5. **Samples** register under the exact key they resolve, for demonstration.

## Alternatives considered

| Option | Why rejected |
|---|---|
| Structured key record (`record ChatClientKey(string Provider, string Model, string? Purpose)`) | Requires consumers to import a type from our library just to register a client. The ergonomic win is small; the coupling is real. |
| Reuse `SemanticKernelPooling`-style "scope" strings (`"openai_default"`, `"openai_fast"`) | Opaque. Nothing in the name tells a reader which model or which purpose. We got burned by this in VAIS2's `_kernelScope`. |
| No convention; let each consumer pick | Works — until two libraries collide on a key. The whole point of a convention is you don't have to negotiate. |
| `Microsoft.Extensions.AI`'s `ChatClientBuilder` chain without keys | Fine for the single-client case, which is already covered by resolving `IChatClient` without a key. Doesn't address the fan-out use case. |

## Consequences

- **Positive:** keys are greppable; tooling-free; fit the existing `.AddKeyedSingleton<IChatClient>(...)` surface without any extension helpers on our side.
- **Positive:** no coupling on a `Vais2.Agents`-owned key type. Consumers can reuse these keys with any DI-aware library that speaks `IChatClient`.
- **Negative:** strings are validated at runtime only. A typo (`"opeanai:gpt-4o"`) fails on the first resolution, not at build. We accept this — consumer code is small and the feedback loop is fast.
- **Negative:** the convention is a convention, not a type-checked contract. Adopters who don't follow it lose the benefit; we don't police it.

## Follow-ups

- When the Orleans host lands (W3 / M3), grain code that needs a provider should accept `[FromKeyedServices(...)]` optional parameters with a documented default key.
- A `LoadBalancingChatClient` decorator (decision §4.8) will fan out across keyed clients given a `IEnumerable<IChatClient>` — we'll document the naming convention it expects from that list there.
- VAIS2 migration (W9) will need a compatibility shim mapping the old `_kernelScope` strings to the new convention. Tracked for W9, not implemented here.
