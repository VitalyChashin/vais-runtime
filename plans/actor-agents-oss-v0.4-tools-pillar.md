# v0.4.0-preview — Tools pillar (§9.6 of the architectural review)

Tactical plan for the sixth pillar. Companion to [`actor-agents-oss-architecture-review.md`](./actor-agents-oss-architecture-review.md) §9.6. Created 2026-04-18.

---

## Scope

Additive helpers that close the "tool authoring is raw / no dynamic discovery" gap without touching the existing `ITool` / `IToolRegistry` surface. Three things ship:

1. **`IToolSource`** — catalogue-style dynamic tool discovery (MCP servers, A2A remotes, custom sources).
2. **`AggregatingToolRegistry`** — static tools + `IToolSource`s combined via a `BuildAsync` factory so the cached `Tools` property stays sync.
3. **`Tool.FromFunc<TInput, TOutput>`** — typed helper that generates `ITool` from a strongly-typed handler using `System.Text.Json.Schema.JsonSchemaExporter` (net9+).

**Design decisions settled 2026-04-18**:

1. **Skip `IToolApprovalPolicy`.** The review listed it as a separate interface, but its semantic surface (pre-invoke decision: allow / deny / defer-to-human) overlaps exactly with the v0.4 `IToolGuardrail.BeforeInvokeAsync` returning `GuardrailOutcome` with Pass / Deny / Interrupt. Shipping a parallel policy type would duplicate the surface without unique value. If design-partner feedback surfaces a concrete gap (e.g., config-driven policy that differs meaningfully from runtime guardrail logic), we revisit.
2. **`AggregatingToolRegistry` uses a `BuildAsync` factory**, not lazy-discovery-on-first-access. Keeps the `IToolRegistry.Tools` sync contract honest — no sync-over-async blocking inside the getter. Consumers who want dynamic refresh ship their own registry implementation.
3. **`Tool.FromFunc` ships two overloads only**: one for `TInput → TOutput` and one no-arg for `Task<TOutput>`. No void-returning tools (tool outputs need to flow back to the next turn). Schema generated via `JsonSchemaExporter.GetJsonSchemaAsNode`, cached per tool instance. Output serialization: `string` → verbatim, `null` → empty string, otherwise JSON.
4. **`IToolRegistry` surface stays unchanged.** The extension is additive via the new `AggregatingToolRegistry` implementation.

---

## Delivery — single PR

**Packages**: `Vais2.Agents.Abstractions`, `Vais2.Agents.Core`.

Tasks:

- [x] Abstractions: `IToolSource.DiscoverAsync(CT) -> IAsyncEnumerable<ITool>`.
- [x] Core: `AggregatingToolRegistry` with `BuildAsync` factory — discovers all sources in order, caches; first-wins on duplicate names.
- [x] Core: static `Tool.FromFunc<TInput, TOutput>` — schema via `JsonSchemaExporter.GetJsonSchemaAsNode`; string output → verbatim, null → empty, other → JSON.
- [x] Core: static `Tool.FromFunc<TOutput>` no-arg overload — empty-object schema (`{"type":"object","properties":{}}`).
- [x] Tests — 13 new: 5 `AggregatingToolRegistryTests` (null-input, static pass-through, source-merge, first-wins-duplicate, cancellation-propagates) + 8 `ToolFromFuncTests` (schema shape, deserialize, string verbatim, object JSON-serialized, null empty, invalid JSON → `ArgumentException`, no-arg overload, end-to-end `StatefulAiAgent` loop).
- [x] `PublicAPI.Unshipped.txt` updates: 2 new entries in Abstractions (`IToolSource` + method), 7 new in Core (registry + Tool type + 2 FromFunc overloads + `BuildAsync`). **Finding**: STJ's `JsonSchemaExporter` emits nullability-aware union types (e.g., `"type": ["string", "null"]`) rather than plain string types. Tests use loose structural assertions; adapter-side schema post-processing (to satisfy OpenAI's strict mode, if needed) is a consumer concern.

Breaking-change ledger: None. Pure additions.

---

## Progress log

- 2026-04-18 — plan created, four design decisions settled (skip IToolApprovalPolicy as redundant with IToolGuardrail, BuildAsync factory vs. lazy, two Tool.FromFunc overloads, IToolRegistry unchanged).
- 2026-04-18 — PR complete on local working tree. `IToolSource` in Abstractions; `AggregatingToolRegistry.BuildAsync` + `Tool.FromFunc<TInput, TOutput>` + no-arg `Tool.FromFunc<TOutput>` in Core. 13 new tests, 238/238 non-container green, 0 warnings.
