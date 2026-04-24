# v0.4.0-preview — Prompt pillar (§9.3 of the architectural review)

Tactical plan for the third pillar. Companion to [`actor-agents-oss-architecture-review.md`](./actor-agents-oss-architecture-review.md) §9.3. Created 2026-04-18.

---

## Scope

Introduce the two prompt-construction plug-points: `IPromptTemplate` (a simple-by-default renderer consumers can swap for SK's Handlebars/Liquid engine) and `ISystemPromptComposer` + `ISystemPromptContributor` (multi-part system-prompt assembly in priority order). No template engine shipped; `FormatStringPromptTemplate` does plain `{variable}` substitution.

**Design decisions settled 2026-04-18**:

1. **Composer replaces `SystemPrompt`** — when `StatefulAgentOptions.SystemPromptComposer` is non-null, `StatefulAiAgent` calls `ComposeAsync` per turn and uses the result as the base system prompt, ignoring the plain `SystemPrompt` string. Context providers' `SystemPromptAddendum` still concatenates on top. Option (a) from the conversation; avoids subtle merge-order questions.
2. **Deviation from review: `IPromptTemplate` is NOT on `StatefulAgentOptions`.** `StatefulAiAgent` doesn't consume `IPromptTemplate` directly — only composers/contributors do, and those resolve it via constructor DI or their own wiring. Putting a dead-weight `PromptTemplate` slot on options would just confuse consumers. `IPromptTemplate` ships as a standalone Abstractions interface + `FormatStringPromptTemplate` Core default; consumers inject where needed.
3. **Contributor priority is ascending** — lower `Priority` runs earlier (matches ASP.NET middleware ordering). `AggregatingSystemPromptComposer` joins non-null/non-empty contributions with `\n\n`.

---

## Delivery — single PR

**Packages**: `Vais2.Agents.Abstractions`, `Vais2.Agents.Core`.

Tasks:

- [x] Abstractions: `IPromptTemplate` — `RenderAsync(string template, IReadOnlyDictionary<string, object?> variables, CT) -> ValueTask<string>`.
- [x] Abstractions: `ISystemPromptComposer` — `ComposeAsync(AgentContext context, CT) -> ValueTask<string?>`.
- [x] Abstractions: `ISystemPromptContributor` — `int Priority { get; }`, `ContributeAsync(AgentContext context, CT) -> ValueTask<string?>`.
- [x] Core: `FormatStringPromptTemplate.Instance` — `{key}` substitution; unknown keys pass through as literal `{key}` text; unmatched `{` emitted verbatim; null variable values → empty; non-string values `.ToString()`'d.
- [x] Core: `AggregatingSystemPromptComposer(IEnumerable<ISystemPromptContributor>)` — orders contributors by `Priority` ascending at construction; joins non-null/non-empty results with `\n\n`; returns null when all contributors yield nothing.
- [x] Core: `StatefulAgentOptions.SystemPromptComposer` (nullable).
- [x] Core: `StatefulAiAgent` — composer call before building the candidate in both `AskAsync` and `StreamAsync`; when non-null, composer result is the base `SystemPrompt`; when null, `SystemPrompt` property is used (unchanged behaviour).
- [x] Tests — 15 new: 6 `FormatStringPromptTemplateTests`, 5 `AggregatingSystemPromptComposerTests`, 4 `StatefulAiAgentComposerIntegrationTests` (composer overrides `SystemPrompt` string, fallback when no composer, addendum concats on composer output, null-composer-result still gets addendum).
- [x] `PublicAPI.Unshipped.txt` updates.
- [ ] Smoketest: prompt-composer segment — deferred to the 0.4 cut.

Breaking-change ledger:

- None. Pure additions. Defaults preserve byte-for-byte behaviour (composer null = `SystemPrompt` string path unchanged).

---

## Progress log

- 2026-04-18 — plan created, three design decisions settled (composer replaces SystemPrompt, IPromptTemplate not on options, ascending priority ordering).
- 2026-04-18 — PR complete on local working tree. Abstractions: `IPromptTemplate`, `ISystemPromptComposer`, `ISystemPromptContributor`. Core: `FormatStringPromptTemplate.Instance`, `AggregatingSystemPromptComposer`, `StatefulAgentOptions.SystemPromptComposer`, `StatefulAiAgent` composer wired in both `AskAsync` and `StreamAsync`. 15 new tests, 173/173 non-container green, 0 warnings.
