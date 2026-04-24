# v0.4.0-preview — Session pillar (§9.1 of the architectural review)

Tactical plan for the first pillar of the architectural review. Companion to [`actor-agents-oss-architecture-review.md`](./actor-agents-oss-architecture-review.md) §9.1. Created 2026-04-18.

---

## Scope

Introduce `IAgentSession` as the canonical conversation-keying primitive. Fold current `IAiAgent.History` into it as a shim. Add the memory-store + history-reducer sidecar. Wire an Orleans session grain that matches the universal per-session single-writer pattern.

**Approved design decision (2026-04-18)**: option (b) — **grain per `(agentId, sessionId)` pair**. Single-writer-per-session, not per-agent. Agent-level state (system prompt, tool bindings) lives in a separate `AgentConfigGrain` read by session grains on activation.

**Out of scope (deferred impl)**: `RedisMemoryStore`, `PostgresMemoryStore`, `VectorMemoryStore`, `SummarisingHistoryReducer`. Land as their own packages post-0.4.

---

## Delivery — three PRs

### PR 1 — Session primitive end-to-end

**Packages**: `Vais2.Agents.Abstractions`, `Vais2.Agents.Core`, `Vais2.Agents.Hosting.Orleans`.

Tasks:

- [x] Abstractions: `IAgentSession` interface (`SessionId`, `AgentId`, `History`, `AppendAsync`, `ResetAsync`).
- [x] Abstractions: add `Session` property to `IAiAgent`.
- [x] Core: `InMemoryAgentSession` default implementation.
- [x] Core: `StatefulAgentOptions.Session` (nullable, defaults to a fresh `InMemoryAgentSession`).
- [x] Core: `StatefulAiAgent` routes history read/append through the session; `Session` and `History` properties both point at it; `Reset()` delegates to `Session.ResetAsync`.
- [x] Core: `StatefulAgentOptions.InitialHistory` — documented interaction with `Session` (both set → throws `ArgumentException`; only `InitialHistory` → seeds the default session).
- [x] Hosting.Orleans: `OrleansAiAgentProxy.Session` shim (`GrainBackedAgentSession` — history live-views proxy cache, `ResetAsync` forwards to grain, `AppendAsync` throws `NotSupportedException` with PR 3 guidance).
- [x] Tests — Core: 15 new tests across `AgentSessionTests` (`InMemoryAgentSession` contract) and `StatefulAiAgentSessionIntegrationTests` (default session, `InitialHistory` seeding, both-throws, supplied-session writes-through, `IAiAgent.History` shim parity, `Reset` clears both views, two agents sharing one session).
- [x] Tests — Hosting.Orleans: existing 16 proxy tests still pass unchanged.
- [x] `PublicAPI.Unshipped.txt` updates: 7 entries in Abstractions, 10 in Core, 0 in Hosting.Orleans (proxy is internal).
- [ ] Smoketest: session-usage segment — deferred to the 0.4 cut at the end of the pillar (per the dependency-upgrade-review precedent).
- [x] Build + full test suite green — **96/96 non-container tests** (Core 61 / Observability 11 / Parity 8 / Hosting.Orleans 16; +15 vs the 81-baseline). Zero warnings.

Breaking-change ledger for PR 1:

- `IAiAgent` gains `Session` required member → source-break for any external `IAiAgent` implementation. Expected to be none in v0.3 (contract was tightly coupled to Core turn machinery); documented in 0.4 notes.
- `IAiAgent.History` behaviour unchanged from caller perspective (still sync `IReadOnlyList<ChatTurn>`); implementation now forwards to `Session.History`.

### PR 2 — Memory store + history reducer

**Packages**: `Vais2.Agents.Abstractions`, `Vais2.Agents.Core`.

Tasks:

- [x] Abstractions: `IMemoryStore` + `MemoryScope` + `MemoryItem` + `MemoryDurability { ShortTerm, LongTerm, Working }` + `MemorySearchResult` + `SearchAsync` + `DeleteAsync` contract.
- [x] Abstractions: `IHistoryReducer`.
- [x] Core: `NullMemoryStore.Instance` (no-op default).
- [x] Core: `InMemoryMemoryStore` — thread-safe, partitioned by full `MemoryScope` record (value equality), case-insensitive substring match in search.
- [x] Core: `NoopHistoryReducer.Instance` (identity, pass-through).
- [x] Core: `StatefulAgentOptions.MemoryStore`, `StatefulAgentOptions.HistoryReducer`.
- [x] Core: `StatefulAiAgent` applies `HistoryReducer.ReduceAsync` to the snapshot before building `CompletionRequest` in both `AskAsync` and `StreamAsync` (no-op by default → zero behaviour change).
- [x] Tests: 13 new — 11 `InMemoryMemoryStoreTests` (roundtrip, scope isolation, durability-as-partition-key, overwrite, delete, search casing/topK/empty-query, unknown-scope, null-store) + 2 `HistoryReducerTests` (noop identity, `LastNReducer` wired into `StatefulAiAgent` observing per-turn truncation while session preserves full history).
- [x] `PublicAPI.Unshipped.txt` updates (65 new entries on Abstractions: record auto-members + enum + 2 interfaces; 26 on Core: impls + options + static `Instance` fields).
- [ ] Smoketest: memory-store + history-reducer segment — deferred to the 0.4 cut.

Breaking-change ledger for PR 2:

- None. Pure additions + opt-in integration in `StatefulAiAgent` (defaults preserve pre-0.4 behaviour byte-for-byte).

### PR 3 — Orleans session grain

**Package**: `Vais2.Agents.Hosting.Orleans`.

Tasks:

- [x] New grain interface `IAgentSessionGrain : IGrainWithStringKey` keyed by `"{agentId}/{sessionId}"`.
- [x] New grain impl `AgentSessionGrain` — `IPersistentState<AgentSessionGrainState>` under `AiAgentGrain.StorageName`; history persisted after every `AppendAsync`.
- [x] `OrleansSessionGrainKey` static helper (`Build` / `Parse`) centralising the `/` encoding + validation (no `/` allowed in either id).
- [x] New grain `IAgentConfigGrain : IGrainWithStringKey` keyed by `agentId`; holds system prompt now, extensible for tool bindings + policy refs later.
- [x] `OrleansAgentSession` — `IAgentSession` proxy: lazy hydration on first `History` access, cache refresh after every `Append`/`Reset`, blocking grain calls from non-grain contexts only (documented).
- [x] `OrleansAgentRuntime.GetSession(agentId, sessionId)` — new method returning `IAgentSession`.
- [x] `OrleansAgentRuntime.GetAgentConfig(agentId)` — new method returning `IAgentConfigGrain`.
- [ ] `OrleansAgentRuntime.GetOrCreate(agentId, sessionId?)` overload — **deferred**. Decision: the existing `IAiAgent`-returning `GetOrCreate` keeps its single-session AiAgentGrain-backed shape; consumers wanting per-session execution compose `StatefulAiAgent` with `runtime.GetSession(...)` directly. An opinionated overload can land later once the LLM-on-silo execution story is settled (control-plane pillar, §9.8).
- [ ] `[Obsolete]` on `AiAgentGrain` — **deferred**. Reason: `AiAgentGrain` still serves the valid "silo-local single-session agent with LLM execution on the silo" use case (different from the new pure-state session grain). Premature to deprecate; revisit once control-plane pillar lands.
- [x] Tests — 22 new: `AgentSessionGrainTests` (5 — append/get roundtrip, reset, different-sessions isolation, concurrent-sessions-of-same-agent, delete clears state), `OrleansAgentSessionProxyTests` (4 — writes-through, lazy hydration, `StatefulAiAgent` composition with durable survival across runtime instances, reset), `AgentConfigGrainTests` (2 — system-prompt roundtrip, config grain isolation from session grains), `OrleansSessionGrainKeyTests` (11 — build/parse roundtrip + 5 malformed-key rejections + 4 invalid-input rejections).
- [x] `PublicAPI.Unshipped.txt` updates: 40 entries in `Hosting.Orleans`.
- [ ] Smoketest: Orleans session segment — deferred to pillar cut.

Breaking-change ledger for PR 3:

- Pure additions. No type deprecations, no signature changes. `AiAgentGrain` remains the legacy all-in-one path; `IAgentSessionGrain` is the new pure-state primitive.

---

## Exit criteria for the pillar

- All three PRs merged to OSS repo `main`.
- Full test suite green (105+ tests + new tests from each PR).
- `PublicAPI.Unshipped.txt` complete and consistent across packages.
- Smoketest at 0.4 exercises all three PRs' surfaces.
- Milestone entry appended to `actor-agents-oss-extraction-research.md` §8.
- Memory snapshot updated.

**Not** a gating criterion: Redis/Postgres memory-store impls. Those ship post-0.4.

---

## Risks / open questions to watch during implementation

1. **`IAgentSession.History` sync vs async.** Keeping it sync to preserve the `IAiAgent.History` shim shape. Implementations must materialise history locally. For Postgres-backed sessions (deferred impl), this means activation-time load + in-memory cache. Documented as part of the interface contract.
2. **Agent-config caching strategy** in PR 3. Start with "read on activation, no invalidation" — acceptable for v0.4 since agent config is low-frequency write. If we later need live config updates, add a grain reminder or broadcast via the existing `IAgentEventBus`.
3. **`InitialHistory` + `Session` interaction.** Throwing when both provided is the least-surprising choice for v0.4; revisit if design-partner feedback pushes for "merge" semantics.
4. **DIM vs required member for `IAiAgent.Session`.** Went with required member (source-break for any external impl); low cost because no one has a reason to implement `IAiAgent` outside Core. Revisit if a design partner complains.

---

## Progress log

- 2026-04-18 — plan created, option (b) grain identity approved.
- 2026-04-18 — PR 1 complete on local working tree. Abstractions: `IAgentSession` + `IAiAgent.Session`. Core: `InMemoryAgentSession`, `StatefulAgentOptions.Session`, `StatefulAiAgent` wired through session (Append/Reset/History). Hosting.Orleans: `OrleansAiAgentProxy.Session` shim via private `GrainBackedAgentSession`. 15 new tests, 96/96 non-container green, 0 warnings.
- 2026-04-18 — PR 1 committed as `d4709d8` on OSS repo `main` (not pushed). Commit cadence: per-PR (matching dependency-upgrade Phases A/B/C pattern).
- 2026-04-18 — PR 2 complete on local working tree. Abstractions: `IMemoryStore`, `MemoryScope`, `MemoryItem`, `MemorySearchResult`, `MemoryDurability`, `IHistoryReducer` (6 types). Core: `NullMemoryStore.Instance`, `InMemoryMemoryStore` (scope-partitioned, substring search), `NoopHistoryReducer.Instance`, `StatefulAgentOptions.MemoryStore` + `HistoryReducer`, `StatefulAiAgent` reducer wiring in both `AskAsync` and `StreamAsync`. 13 new tests, 109/109 non-container green, 0 warnings.
- 2026-04-18 — PR 2 committed as `227727d` on OSS repo `main` (not pushed).
- 2026-04-18 — PR 3 complete on local working tree. `Hosting.Orleans`: `IAgentSessionGrain` + `AgentSessionGrain` (pure-state session container), `IAgentConfigGrain` + `AgentConfigGrain` (per-agent shared config), `OrleansSessionGrainKey` (`/` encoding helper), `OrleansAgentSession` client proxy, `OrleansAgentRuntime.GetSession` + `GetAgentConfig`. Intentionally deferred: `GetOrCreate` overload for per-session agents (waits on control-plane pillar), `[Obsolete]` on `AiAgentGrain` (still serves silo-local single-session use case). 22 new tests, 131/131 non-container green, 0 warnings.
- 2026-04-18 — PR 3 committed as `a56bf19` on OSS repo `main` (not pushed). **Session pillar closed.** Main research doc §8 updated with a pillar-landed entry. Memory snapshot updated. Next: context pillar (§9.2).
- _(PR 3 entry to follow)_
- _(pillar closure entry to follow)_
