# v0.4.0-preview — Context pillar (§9.2 of the architectural review)

Tactical plan for the second pillar of the architectural review. Companion to [`actor-agents-oss-architecture-review.md`](./actor-agents-oss-architecture-review.md) §9.2. Created 2026-04-18.

---

## Scope

Introduce `IContextProvider` + `ContextContribution` + `IContextWindowPacker` as the typed, composable way to contribute per-turn context to an agent's request. Migrate the existing `KnowledgeRetrievalFilter` onto this shape with an `[Obsolete]` shim. No built-in tokenizer-based packer (that's a deferred impl).

**Design decisions settled 2026-04-18**:

1. **`ContextInvocationContext`** is a plain record passed to providers: `(CompletionRequest Candidate, AgentContext AmbientContext, IAgentSession Session)`. Providers inspect the candidate and return a `ContextContribution`; they never mutate.
2. **`InjectedHistory` ordering** — **appended after** session history. Matches the canonical "here's retrieved context / extra examples / prior-turn transcripts" pattern, and keeps the most-recent user turn at the end where models expect it.
3. **Failure handling** — provider exceptions **propagate and fail the turn**. Context providers are load-bearing (a failed retrieval means the agent answers without critical context); swallow semantics can be added per-provider by a consumer wrapper.

---

## Delivery — two PRs

### PR 1 — Context provider chain + window packer + `StatefulAiAgent` wiring

**Packages**: `Vais2.Agents.Abstractions`, `Vais2.Agents.Core`.

Tasks:

- [x] Abstractions: `IContextProvider` (async, returns `ContextContribution`).
- [x] Abstractions: `ContextContribution` record — `SystemPromptAddendum`, `InjectedHistory`, `AdditionalTools` (all optional); static `Empty` instance.
- [x] Abstractions: `ContextInvocationContext` record — `Candidate`, `AmbientContext`, `Session`.
- [x] Abstractions: `IContextWindowPacker` — takes a `CompletionRequest`, returns a (possibly shrunk) `CompletionRequest`. No `modelContextWindow` parameter; packer impls own their own config.
- [x] Core: `NoopContextWindowPacker.Instance` identity default.
- [x] Core: `StatefulAgentOptions.ContextProviders` (ordered `IReadOnlyList`, default empty), `StatefulAgentOptions.ContextWindowPacker` (nullable, defaults to `NoopContextWindowPacker.Instance`).
- [x] Core: `StatefulAiAgent` new pipeline stage between the history reducer and the filter chain — in both `AskAsync` and `StreamAsync`: build candidate → invoke providers → merge (system-prompt addendum with `\n\n`, history appended after candidate, tools concatenated) → apply packer → proceed into filter chain.
- [x] Tests — Core: 11 new in `ContextProviderTests` — zero-providers unchanged, single addendum with separator, addendum with no base, multi-provider concatenation order, injected-history appended after session, tool merging with existing registry, empty-contribution no-op, provider-exception propagation, packer-runs-after-providers, `NoopContextWindowPacker` identity, session passed through `ContextInvocationContext`.
- [x] `PublicAPI.Unshipped.txt` updates in both packages.
- [ ] Smoketest: context-provider segment — deferred to the 0.4 cut.

Breaking-change ledger for PR 1:

- None. Pure additions. Defaults preserve byte-for-byte behaviour.

### PR 2 — Migrate `KnowledgeRetrievalFilter` → `KnowledgeRetrievalContextProvider`

**Package**: `Vais2.Agents.Persistence.VectorData`.

Tasks:

- [x] New `KnowledgeRetrievalContextProvider : IContextProvider` — same retrieval + system-prompt-addendum behaviour; returns `ContextContribution.Empty` when no user turn or zero chunks, and `new ContextContribution(SystemPromptAddendum: contextBlock)` otherwise.
- [x] `[Obsolete("Use KnowledgeRetrievalContextProvider. KnowledgeRetrievalFilter will be removed in v0.5.", DiagnosticId="VAIS2_0001")]` on `KnowledgeRetrievalFilter`. Filter stays fully functional for one release window.
- [x] Existing `KnowledgeRetrievalOptions` shared between the filter and the new provider (kept non-obsolete).
- [x] Tests — 6 new `KnowledgeRetrievalContextProviderTests` mirroring the filter-tests assertions (system-prompt augmentation, no-user-turn → Empty, zero-chunks → Empty, null-prompt handling, custom template + separator, latest-user-turn-wins). Old filter tests kept alive with `#pragma warning disable VAIS2_0001` at file scope.
- [x] `PublicAPI.Unshipped.txt` gets 3 entries for the new provider.

Breaking-change ledger for PR 2:

- `KnowledgeRetrievalFilter` → `[Obsolete]`; removal planned for v0.5. Consumers on the filter path get a compile-time warning with `VAIS2_0001` as the diagnostic id + the migration guidance string.

---

## Exit criteria for the pillar

- Both PRs merged to OSS repo `main`.
- Full test suite green (131+ tests + new).
- `PublicAPI.Unshipped.txt` complete across both packages.
- Plan doc's task-list checkboxes all ticked.
- Milestone entry appended to `actor-agents-oss-extraction-research.md` §8.
- Memory snapshot updated.

---

## Progress log

- 2026-04-18 — plan created, three design decisions settled (ContextInvocationContext shape, InjectedHistory ordering = append after session, failure handling = propagate).
- 2026-04-18 — PR 1 complete on local working tree. Abstractions: `IContextProvider`, `ContextContribution`, `ContextInvocationContext`, `IContextWindowPacker`. Core: `NoopContextWindowPacker.Instance`, `StatefulAgentOptions.ContextProviders` + `ContextWindowPacker`, `StatefulAiAgent` provider+packer stage wired in both `AskAsync` and `StreamAsync`. 11 new tests, 142/142 non-container green, 0 warnings.
- 2026-04-18 — PR 1 committed as `a6067ae` on OSS repo `main` (not pushed).
- 2026-04-18 — PR 2 complete on local working tree. Persistence.VectorData: new `KnowledgeRetrievalContextProvider` (mirrors filter semantics, returns `ContextContribution`), `[Obsolete]` with `DiagnosticId="VAIS2_0001"` on the legacy `KnowledgeRetrievalFilter` (still functional), 6 new provider tests, old 6 filter tests kept alive under `#pragma warning disable VAIS2_0001`. 158/158 non-container green, 0 warnings.
- 2026-04-18 — PR 2 committed as `5efdacd` on OSS repo `main` (not pushed). **Context pillar closed.** Main research doc §8 updated. Next pillar: prompt construction (§9.3).
- _(PR 2 entry to follow)_
- _(pillar closure entry to follow)_
