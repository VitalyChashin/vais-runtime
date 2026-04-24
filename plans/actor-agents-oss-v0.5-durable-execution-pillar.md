# v0.5.0-preview — Durable-execution pillar (post-v0.4 follow-up to architectural review §7)

Lightweight tactical plan for the first pillar after the v0.4 cut. Companion to [`actor-agents-oss-architecture-review.md`](./actor-agents-oss-architecture-review.md) §7 and [`actor-agents-oss-extraction-research.md`](./actor-agents-oss-extraction-research.md) §7. Created 2026-04-19.

---

## Scope

Give the agent loop a journal so a run can survive a crash or a human-in-the-loop pause and pick up without re-invoking already-done tool calls. Replace today's `ResumeAsync` shim (forwards payload as a new user turn) with true mid-loop resume anchored on `IToolCallDispatcher` + `IAgentSession`.

**MVP boundary (locked 2026-04-19):**

1. **Journal granularity = tool-call only.** Record every `ToolCallRequest`/`ToolCallOutcome` pair dispatched during a run. No per-turn journaling, no event-sourced journal.
2. **Replay semantics = cache-replay within a run.** On resume, journaled tool outcomes are returned from the journal without re-invoking the tool. Tools are documented as "idempotent where possible"; no `Pure`/`SideEffecting` type tags.
3. **Determinism boundary = provider + tool calls only.** Context providers and guardrails re-run on replay; they're documented as "expected to be deterministic within a run".
4. **`RunId` is a first-class primitive.** Generated at `AskAsync`/`StreamAsync` entry. Carried on `AgentContext`. Used to scope journal entries.
5. **`IAgentJournal` is a new Abstractions type.** Wired through `StatefulAgentOptions.Journal`; `DefaultToolCallDispatcher` reads/writes it. Decorator-free to keep the call-site obvious (dispatcher owns the persistence seam).
6. **`ResumeAsync` becomes a real operation.** Reloads journal entries for `(SessionId, RunId)`, rebuilds working history, fast-forwards past recorded tool calls, re-enters the loop at the interrupt point with `resumeInput.Payload` routed as the interrupt response.
7. **`SignalAsync = ResumeAsync` for v0.5.** The control-plane `IAgentLifecycleManager.SignalAsync` verb is specced as the Temporal-style resume-with-data; in v0.5 the single implementation is the resume flow. Separation comes with cloud runtime (Phase 3).
8. **Orleans impl lives in `Vais.Agents.Hosting.Orleans`.** `OrleansAgentJournal` is a separate grain keyed by `(agentId, sessionId, runId)` — not baked into the session grain. Session grain stays the clean user/assistant history; journal grain holds the in-flight step log.
9. **Journal stays agent-neutral in shape.** Entries are open enough that a future `GraphOrchestrator` or multi-agent runtime can reuse the same journal primitive without a re-design.
10. **Explicitly deferred**: timers (`Sleep`/`ContinueAsNew`), child workflows, versioned execution, cross-run replay, journal compaction, Postgres/Redis `IAgentJournal` impls, streaming resume. None of these block a v0.5 cut.

---

## Delivery

### PR 1 — `IAgentJournal` abstractions + defaults

**Packages**: `Vais.Agents.Abstractions`, `Vais.Agents.Core`.

Tasks:

- [x] Abstractions: `JournalEntry` sealed closed hierarchy — `ToolCallRecorded(RunId, CallId, ToolName, Arguments, Outcome, At)` as the v0.5 only subclass; abstract base carries `RunId` + `At`.
- [x] Abstractions: `IAgentJournal` — `ValueTask AppendAsync(JournalEntry entry, CancellationToken ct)`; `IAsyncEnumerable<JournalEntry> ReadAsync(string runId, CancellationToken ct)`; `ValueTask ClearAsync(string runId, CancellationToken ct)`.
- [x] Abstractions: `RunId` stays `string` — no separate type. Documented ASCII-only opaque identifier; runtime picks the format.
- [x] Core: `NullAgentJournal.Instance` — no-op; `ReadAsync` returns empty.
- [x] Core: `InMemoryAgentJournal` — `ConcurrentDictionary<string /*runId*/, List<JournalEntry>>`; append is lock-scoped per run; read is a snapshot copy.
- [x] Core: `StatefulAgentOptions.Journal` — nullable; defaults to `NullAgentJournal.Instance` when unset.
- [x] Tests — 11 new (landed a bit over the plan's 6): null journal no-ops, round-trip, ordered append-replay, clear-per-run, clear-of-unknown, concurrent cross-run appends, concurrent same-run appends, null-entry rejection, unknown-run read, options-default-null, options-set-via-init.
- [x] `PublicAPI.Unshipped.txt` updates — 40-odd lines for record auto-synthesised members on the abstract-base + sealed-subclass pattern (same cost as `AgentEvent` subclasses).

Breaking-change ledger for PR 1:

- None. Pure additions. No dispatcher changes yet — `StatefulAiAgent` doesn't touch the journal in this PR.

### PR 2 — Journal wiring in `DefaultToolCallDispatcher`

**Packages**: `Vais.Agents.Core`.

Tasks:

- [x] Extend `DefaultToolCallDispatcher` ctor with optional `IAgentJournal? journal = null`. `StatefulAiAgent` passes `options.Journal` when building the default dispatcher.
- [x] Dispatcher write path: after a successful or failed `ToolCallOutcome`, append a `ToolCallRecorded(RunId, CallId, ToolName, Arguments, Outcome, Now)` entry. Journal-append exceptions propagate (journal is load-bearing — swallow semantics are a consumer wrap).
- [x] Dispatcher read path: before invoking a tool, check journal for a prior `ToolCallRecorded` matching `(RunId, CallId)`. If hit, return the recorded outcome directly — skip guardrails (already applied when recorded), skip tool invocation, skip event emission for the cached replay. Cache-replay entries are silent on the event bus in PR 2; `ToolCallReplayed` event lands in PR 4.
- [x] `RunId` source: dispatcher reads `AgentContext.RunId`. Nullable-for-compat — dispatcher skips journal entirely when null (v0.4 callers unchanged). `AgentContext.RunId` added as an init-only property in this PR instead of PR 3, because the dispatcher had to read it; PR 3 still owns the factory + `StatefulAiAgent`-level generation.
- [x] Tests — 11 new (landed a bit over the plan's 5): null RunId = no journal I/O; null journal with RunId = no I/O either; success/tool-exception/unknown-tool outcomes all journaled; crash-then-replay across fresh dispatcher instances; cache-replay bypasses guardrails; cache-replay silent on event bus; journal-append failure propagates; distinct CallIds get distinct invocations; end-to-end `StatefulAiAgent.AskAsync` with `options.Journal` wires through.
- [x] `PublicAPI.Unshipped.txt` updates — `DefaultToolCallDispatcher` ctor `*REMOVED*`+new; `AgentContext.RunId` get/init.

Breaking-change ledger for PR 2:

- `DefaultToolCallDispatcher` ctor gains optional trailing `IAgentJournal? journal = null`. Source-compat.
- Behavior change visible only when `options.Journal` is wired — v0.4 consumers without journal see identical behavior.

### PR 3 — `RunId` + `AgentContext` extension

**Packages**: `Vais.Agents.Abstractions`, `Vais.Agents.Core`.

Tasks:

- [x] Abstractions: extend `AgentContext` with `string? RunId` (nullable-for-compat, documented as populated by `StatefulAiAgent` at run entry). **Landed in PR 2** so the dispatcher could read it.
- [x] Core: `StatefulAiAgent.AskAsync` + `StreamAsync` generate a `RunId` (default: `Guid.NewGuid().ToString("N")`) and stamp it on the ambient `AgentContext` for the duration of the run. Caller-supplied `RunId` short-circuits the factory — the resume flow (PR 4) will use that to thread the interrupted run's id back in.
- [x] Core: `StatefulAgentOptions.RunIdFactory` — optional `Func<string>` for consumers who need structured RunIds (ULID/KSUID/Snowflake). Lives on `StatefulAgentOptions` (Core), not Abstractions — the factory type is trivially neutral, no need to pull `System.Func<string>` into the public abstractions surface.
- [x] `IAgentSession` unchanged — RunId-to-session association lives on journal entries, not session state. Keeps `IAgentSession` a clean history boundary.
- [x] Tests — 7 new (landed above the plan's 4): AskAsync stamps a RunId; two separate Asks get distinct RunIds; caller-supplied RunId wins; custom factory used when no caller id; StreamAsync stamps too; same RunId threaded to every tool dispatch in one run; default factory produces 32-hex string.
- [x] `PublicAPI.Unshipped.txt` updates.

**Design note: ambient accessor not mutated.** The stamped context is passed by value to dispatchers / guardrails / events — the `IAgentContextAccessor.Current` still returns whatever the caller put there. Less magic, same observable effect for every integration point that already takes `AgentContext` as a parameter.

Breaking-change ledger for PR 3:

- `AgentContext` gains a nullable property — additive, no ctor change.
- `StatefulAgentOptions` gains a nullable factory — additive.

### PR 4 — True `ResumeAsync` + `ToolCallReplayed` event

**Packages**: `Vais.Agents.Abstractions`, `Vais.Agents.Core`, `Vais.Agents.Hosting.Orleans`.

Tasks:

- [x] Abstractions: `ResumeInput` gains nullable `RunId` as an **init-only property** (not a positional param). Avoids the PublicAPI `*REMOVED*` churn that a positional shape change would require; keeps the existing ctor + Deconstruct shipped entries intact. When null → shim semantics; when set → cache-replay.
- [x] Abstractions: `AgentInterrupt` gains nullable `RunId` as an init-only property. Populated by `StatefulAiAgent` + the dispatcher before throwing `AgentInterruptedException`; guardrail authors don't touch it.
- [x] Abstractions: `ToolCallReplayed(At, Context, CallId, ToolName)` — 9th `AgentEvent` subclass. No `Succeeded` field (replays always succeeded originally — that's why they're in the journal).
- [x] Core: `StatefulAiAgent.ResumeAsync` rewritten. **Simplified v0.5 approach** (design narrowed during implementation): resume extracts the payload as a user-turn string and calls a new private `AskAsyncCore(userMessage, runIdOverride, ct)` threading `resumeInput.RunId` through the `StampRunId` precedence chain (override > ambient > factory). The dispatcher's cache-replay path picks up any journaled tool outcomes matching the LLM's new tool-call `CallId`s. **Not done**: session-scoped resume (assert same session), working-history reconstruction from journaled assistant turns — deferred to a follow-up because the current journal granularity is tool-call-only (by MVP design). The chosen approach achieves the v0.5 goal (survive HITL/crash without double-firing side effects) with minimal scope; the LLM's non-determinism across resume is documented.
- [x] Core: guardrail/tool interrupt sites stamp `RunId` from context via `outcome.InterruptPayload with { RunId = context.RunId }`.
- [x] Hosting.Orleans: `AgentEventSurrogate` — `AgentEventKind.ToolCallReplayed = 8` + `ToolCallReplayedSurrogateConverter` (M3e-3b pattern). Reused existing `CallId` + `ToolName` surrogate fields — no new fields needed. **Also**: `AgentContextSurrogate` gained `RunId` (`Id(4)`) so the event bus / grain-state round-trips preserve it end-to-end.
- [x] Tests — 13 new total (plan asked for 10): 12 in Core (`DurableResumeTests.cs`) + 1 Orleans `ToolCallReplayed` round-trip (also verifies `AgentContext.RunId` survives the wire). Plus updated the PR 2 test `Cache_Replay_Is_Silent_On_Event_Bus` → `Cache_Replay_Emits_Single_ToolCallReplayed_Event`.
- [x] `PublicAPI.Unshipped.txt` updates across three packages (Abstractions, Core, Hosting.Orleans).

Breaking-change ledger for PR 4:

- **Zero `*REMOVED*` markers**: `AgentInterrupt.RunId` and `ResumeInput.RunId` landed as init-only properties, not positional ctor params. Keeps existing source callers compiling.
- `AgentEvent` hierarchy gains a 9th subclass (`ToolCallReplayed`). Consumers pattern-matching exhaustively need to add a new case — same class of additive change as every prior event addition.
- `ResumeAsync` behaviour: when `ResumeInput.RunId` is set, cache-replay kicks in; when null, same v0.4 shim semantics. No source break.
- `AgentInterrupt.RunId`: agent-raised interrupts now populate it; hand-constructed interrupts default to null. No source break.

Breaking-change ledger for PR 4:

- **`ResumeInput` shape break**: new required positional `RunId`. Consumers on v0.4's shim must add the `RunId` arg. v0.4 is pre-release so this is an acceptable preview-to-preview change. Documented in release notes.
- **`AgentInterrupt` shape break**: new `RunId` positional param. Same rationale. Consumers reading the record will need to recompile.
- **`ResumeAsync` behavior break**: shim's "forward payload as user turn" semantics replaced with mid-loop resume. Callers relying on the shim's behavior must adopt the new contract or stay on v0.4.
- `AgentEvent` hierarchy gains `ToolCallReplayed` — 9 subclasses total.

### PR 5 — Orleans journal grain + v0.5.0-preview cut

**Packages**: `Vais.Agents.Hosting.Orleans`, repo-level packaging + tag.

Tasks:

- [x] Hosting.Orleans: `IAgentRunJournalGrain` + `AgentRunJournalGrain` keyed by the opaque `RunId` string (globally-unique GUID-hex; no compound encoding needed because the journal API is run-scoped). Grain state is `List<JournalEntrySurrogate>` behind `IPersistentState` on the shared `AiAgentGrain.StorageName` (`vais.agents`) storage.
- [x] Hosting.Orleans: `OrleansAgentJournal : IAgentJournal` client proxy that routes `AppendAsync` / `ReadAsync` / `ClearAsync` to the grain. `OrleansAgentRuntime.GetJournal()` factory helper (no per-RunId args — journal is itself run-scoped internally).
- [x] Hosting.Orleans: `JournalEntrySurrogate` + `JournalEntrySurrogateConverter` + `ToolCallRecordedSurrogateConverter` (M3e-3b pattern). Flattens the nested `ToolCallOutcome` into primitive fields so the Abstractions package stays Orleans-free.
- [x] Hosting.Orleans (shape pivot mid-PR): grain interface carries `JournalEntrySurrogate` on the wire rather than `JournalEntry`. Orleans' surrogate dispatch on polymorphic abstract parameters across grain RPC hit a `ReferenceNotFoundException` during serialisation; the struct-typed wire sidesteps the issue entirely. Conversion between `JournalEntry` and `JournalEntrySurrogate` happens in `OrleansAgentJournal` at the boundary — public `IAgentJournal` contract unchanged. **Finding** below in the log.
- [x] Integration test: cross-process-style resume — pre-seed the Orleans journal for a RunId, then call `ResumeAsync` with the same RunId, assert the tool is NOT invoked (outcome served from the silo). Plus 8 direct grain tests for append/read/order/isolation/activation-collection rehydration/clear/JsonElement round-trip/runtime factory.
- [x] API freeze: `Unshipped` → `Shipped` across Abstractions + Core + Hosting.Orleans. `*REMOVED*` markers from v0.4.1 `CompletionUpdate` resolved by deleting the matching original lines from `Shipped.txt` (not carrying the marker forward).
- [x] Pack: `dotnet pack -c Release -p:VersionPrefix=0.5.0 -p:VersionSuffix=preview -o artifacts/packages` → **13 `.nupkg` + 13 `.snupkg`** at `0.5.0-preview`.
- [x] Smoketest: bumped to `0.5.0-preview`; added a "Durable" segment exercising `NullAgentJournal` + `InMemoryAgentJournal` + `ToolCallRecorded` + `ToolCallReplayed` + `AgentContext.RunId` + `AgentInterrupt.RunId` + `ResumeInput.RunId`. Extended Hosting.Orleans smoke line with `IAgentRunJournalGrain` + `JournalEntrySurrogate` type probes. Wired `Journal` + `RunIdFactory` onto the `StatefulAiAgent` composition. Clean restore + build + run.
- [x] Tag: annotated `v0.5.0-preview` on OSS repo `main`. **Not pushed** — same pattern as v0.1 through v0.4.
- [x] Milestone log entry + §8 findings update — the research doc's §7 "Next action" block gets a note that the durable-execution pillar is closed, and §8 picks up the "Orleans polymorphic grain-RPC surrogate dispatch" finding.

Breaking-change ledger for PR 5:

- None beyond PR 4. This is packaging + Orleans wiring.

---

## Exit criteria for the pillar

- [ ] All five PRs on OSS repo `main` (not pushed).
- [ ] `ResumeAsync` is a real mid-loop resume; the v0.4 shim comment is gone.
- [ ] `IAgentJournal` ships with `NullJournal` + `InMemoryJournal` + `OrleansAgentJournal` defaults.
- [ ] Full non-container test suite green; Orleans cross-process resume test green.
- [ ] `v0.5.0-preview` tag created; 13 packages packed.
- [ ] Smoketest re-runs clean including the new resume segment.
- [ ] Milestone log entry + §8 findings update in the research doc.

---

## Progress log

- 2026-04-19 — plan created, MVP boundary locked (10 decisions per recommended start point).
- 2026-04-19 — PR 1 complete on local working tree. `IAgentJournal` + `JournalEntry` + `ToolCallRecorded` in Abstractions; `NullAgentJournal.Instance` + `InMemoryAgentJournal` in Core; `StatefulAgentOptions.Journal` slot. 11 new tests in `Vais.Agents.Core.Tests/AgentJournalTests.cs`. Core suite: 197 tests passing (+11). Full non-container surface: Core 197 + Observability 11 + Hosting.Orleans 43 + Persistence.Redis 7 + Persistence.Postgres 5 + CrossHost 2 + Parity 10 + VectorData 16 + A2A 12 + Mcp 2 = **305 passing, 0 failed**. Build clean (0 warnings). No adapter or orchestrator changes; `StatefulAiAgent` still doesn't consult the journal — that's PR 2.
- 2026-04-19 — pre-pillar housekeeping landed on OSS repo `main` (four commits, none pushed):
  1. `7d7af66` — brand rename (`Vais2.Agents.*` → `Vais.Agents.*`) bundled with the two post-v0.4 follow-ups (A2A 1.0.0-preview2 bump, tool-using streaming). 263 files; bundled because both content rewrites sit *inside* the renamed files and separating them would mean resurrecting the old Vais2 paths just to drop them again.
  2. `1e03ccd` — documentation set (31 new files across `docs/concepts/`, `docs/getting-started/`, `docs/guides/`, `docs/reference/`, + `docs/adr/index.md`).
  3. `54f9aff` — samples set (69 files; 20 new samples + shared `Directory.Build.props` / `Directory.Packages.props` / `NuGet.config` scoping).
  4. `0cb5d81` — **v0.5 PR 1: `IAgentJournal` abstractions + in-proc defaults** (8 files, +454 LOC). Landed on top of housekeeping as the first pillar PR. Tag `v0.4.0-preview` still points at `9c73a4b` — not yet moved; a `v0.5.0-preview` tag lands with PR 5.
- 2026-04-19 — **v0.5 PR 2 landed** as `156e8a3` on OSS repo `main` (not pushed). `DefaultToolCallDispatcher` now reads + writes `IAgentJournal` when the caller has stamped `AgentContext.RunId`; `AgentContext` gained an init-only `RunId` property (6 files, +450 LOC, +11 tests). Pulled `AgentContext.RunId` into this PR instead of PR 3 because the dispatcher had to read it to function; PR 3 still owns the `StatefulAiAgent`-level generation + `RunIdFactory` option. Core 208 passing (+11). Full non-container suite: 316 passing (Core 208, Observability 11, Hosting.Orleans 43, Persistence.Redis 7, Persistence.Postgres 5, CrossHost 2, Parity 10, VectorData 16, A2A 12, Mcp 2). 0 warnings. No consumer-visible behaviour change when `options.Journal` is unset.
- 2026-04-19 — **v0.5 PR 3 landed** as `87eac26` on OSS repo `main` (not pushed). `StatefulAiAgent` now stamps `context.RunId` on every `AskAsync`/`StreamAsync` invocation via a `StampRunId` helper; `StatefulAgentOptions.RunIdFactory` lets consumers override the default `Guid.NewGuid().ToString("N")` with structured ids (ULID/KSUID/Snowflake). Caller-supplied `RunId` on the ambient accessor wins — critical for the resume flow. Ambient `IAgentContextAccessor` is not mutated; the stamped context is passed by value to every downstream seam (dispatcher, guardrails, events, usage sink). 4 files, +284 LOC, +7 tests. Non-container suite: 323 passing (Core 215 +7). 0 warnings.
- 2026-04-19 — **v0.5 PR 4 landed** as `902e244` on OSS repo `main` (not pushed). True resume lands: `AgentInterrupt`/`ResumeInput` gain init-only `RunId` properties (no `*REMOVED*` churn); `StatefulAiAgent.ResumeAsync` threads the caller-supplied `RunId` into a new private `AskAsyncCore(userMessage, runIdOverride, ct)` so the dispatcher's cache-replay path lights up for any journaled tool outcomes that match the LLM's new tool-call `CallId`s; agent + dispatcher stamp `context.RunId` onto `AgentInterrupt` before throwing. `ToolCallReplayed` lands as the 9th `AgentEvent` subclass, emitted on cache hits instead of the silent-replay behaviour from PR 2. Orleans surrogate gains `AgentContextSurrogate.RunId = Id(4)` + `AgentEventKind.ToolCallReplayed = 8` + `ToolCallReplayedSurrogateConverter`. 12 files, +615 LOC, +13 tests. Non-container suite: 336 passing (Core 227 +12; Hosting.Orleans 44 +1). 0 warnings. **Design narrowing**: the plan originally called for session-scoped assertion + working-history reconstruction; shipped the simpler "thread RunId, let cache-replay do the work" approach because the journal is still tool-call-only granularity (MVP scope). Full mid-loop replay remains a follow-up if design partners surface a use case that needs it.
- 2026-04-19 — **v0.5 PR 5 landed** as `c50ac46` on OSS repo `main` (not pushed) + annotated tag **`v0.5.0-preview`**. Closes the pillar. `IAgentRunJournalGrain` + `AgentRunJournalGrain` (IPersistentState-backed, keyed by `RunId`) + `OrleansAgentJournal : IAgentJournal` + `JournalEntrySurrogate` + two converters + `OrleansAgentRuntime.GetJournal()`. 6 new files in Hosting.Orleans, +Context surrogate field round-trip, +9 tests. API freeze: all v0.5 Unshipped promoted to Shipped across Abstractions + Core + Hosting.Orleans; CompletionUpdate `*REMOVED*` markers resolved by deleting originals. Pack: 13 `.nupkg` + 13 `.snupkg` at `0.5.0-preview` in `artifacts/packages/`. Smoketest bumped to `0.5.0-preview` with a new Durable segment + Orleans grain/surrogate probes; ran cleanly. Non-container suite: **345 passing** (Core 227, Hosting.Orleans 53 +9, rest unchanged). 0 warnings.
  - **Finding — Orleans grain RPC doesn't love polymorphic surrogate dispatch on abstract-typed parameters.** First cut had `IAgentRunJournalGrain.AppendAsync(JournalEntry)` with the `[RegisterConverter]` base-plus-concrete pattern that works fine for `AgentEvent` on `IAsyncStream`. Across grain RPC it blew up with `Orleans.Serialization.ReferenceNotFoundException: Reference with id 2 and type Vais.Agents.ToolCallRecorded not found`. Streams handle polymorphic encoding; grain RPC's reference tracking doesn't. Fix: flip the wire type to the `[GenerateSerializer]` struct (`JournalEntrySurrogate`) and do the convert inside `OrleansAgentJournal`. Public `IAgentJournal` shape unchanged. **Documented** for future Orleans integrations that want to carry closed-hierarchy types across grain boundaries — stream or flatten, don't send abstracts over RPC.

**Pillar closed. Post-v0.5 decision point pending**: design-partner feedback round vs. public NuGet push, same as v0.4 — plus the Temporal-parity roadmap note added in the main research doc §7.
