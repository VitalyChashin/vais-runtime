# v0.10 Streaming filter + resilience pipeline — research spike

Scoped research pass before committing to a v0.10 pillar plan. Companion to [`actor-agents-oss-extraction-research.md`](./actor-agents-oss-extraction-research.md) §7 (backlog: *"Streaming-filter pipeline + resilience-pipeline wrapping on streamed turns (the synchronous `IAgentFilter` chain + Polly pipeline are still bypassed on `StreamAsync`, same as v0.4; consumers needing filters stay on `AskAsync`). Needs a streaming-filter surface design."*). Created 2026-04-20.

---

## Why a spike before a pillar

The gap has been tagged as "needs a streaming-filter surface design" since v0.4 shipped. The v0.3/M3e-2 workaround — a separate `IStreamingAgentFilter` with `OnStreamDeltaAsync` + `OnStreamCompleteAsync` — covers per-delta transforms (PII scrubbing, telemetry) but intentionally sidesteps the two harder concerns that motivated `IAgentFilter` in the first place:

1. **Around-provider wrapping** (short-circuit, request rewrite, response materialisation) — request→response-shaped, doesn't translate to `IAsyncEnumerable<CompletionUpdate>` without buffering the entire stream.
2. **Polly `ResiliencePipeline`** retries — streaming consumers have already observed yielded deltas by the time a mid-stream failure hits; naïve retry is unsound.

Both land on the streaming path today through documented "known gaps" (`StatefulAiAgent.cs:320-328`), and both have design options with non-obvious trade-offs. A focused 1-day spike de-risks the pillar scoping the same way the v0.9 graph spike did — research + archetype exercises + findings doc, no public surface change.

The spike is **time-boxed research + archetype design**, not a shippable package. Output: findings doc + archetype consumer sketches. No changes to the shipped library. No new public types added to `main`.

---

## Current state (confirmed before spike)

Verified in the codebase as of 2026-04-20 (`v0.9.0-preview` on OSS `main`):

- **`AskAsync`** wraps provider invocation in `ResiliencePipeline.ExecuteAsync(...)` → `InvokeThroughFiltersAsync` → `_provider.CompleteAsync(...)` (`StatefulAiAgent.cs:187-189` + `:960-976`). Filters implement `IAgentFilter.InvokeAsync(req, next, ct) : Task<CompletionResponse>`.
- **`StreamAsync`** calls `streamingProvider.StreamAsync(request, ct)` directly (`:445`). Polly pipeline + `IAgentFilter` chain are bypassed.
- **`IStreamingAgentFilter`** (`Vais.Agents.Abstractions/IStreamingAgentFilter.cs`) shipped in v0.3/M3e-2 — per-delta `OnStreamDeltaAsync(update, ct) : ValueTask<CompletionUpdate>` (default pass-through) + `OnStreamCompleteAsync(final, ct) : ValueTask` (default no-op). Wired into `StreamAsync` at `:477-495` (per delta) and `:567-585` (end of stream).
- Tool-using streaming (post-v0.4 follow-up) adds an outer tool-call loop around `StreamAsync` — each "turn" opens a fresh provider enumerator. `_streamingFilters` runs on every turn's deltas; the Complete hook fires once at end of the final (non-tool-call) turn.

No existing hook wraps the provider-streaming call itself. No retry of any kind on `StreamAsync`. The pillar has to close both gaps.

---

## Four blocking questions

1. **Q1 — Filter surface shape.** `IAgentFilter` returns `Task<CompletionResponse>`; streaming adapters can't trivially implement it without buffering the whole stream. Options: (a) a new `IStreamingProviderFilter` whose `InvokeAsync(req, next, ct)` returns `IAsyncEnumerable<CompletionUpdate>`; (b) widen the shipped `IStreamingAgentFilter` with a third DIM method doing the same, keeping per-delta + end-of-stream on the same type; (c) default-adapt `IAgentFilter` onto streaming by buffering (defeats streaming). **Decision axis**: one streaming-filter type with three override points vs. two types with distinct roles. Lean **(b)** going in — matches `IAgentFilter`'s around-provider role and keeps registration simple; needs validation that PublicAPI analyzer is OK with a DIM addition on a shipped interface. **Blocker**: resolves the public-surface shape; everything else composes around it.

2. **Q2 — Retry semantics on a producer that has emitted to a consumer.** Polly wraps `Func<CT, ValueTask<T>>`; streams aren't cleanly retryable once deltas are yielded. Options: (a) retry only the **pre-first-delta window** (open enumerator + first `MoveNextAsync`) — transient connect/429/DNS retries stay useful, yielded = committed; (b) buffer the whole stream and only yield once it drains fully (degrades streaming → request→response); (c) no retry on the streaming path, document the asymmetry. Lean **(a)** — keeps streaming actually streaming while recovering the 80% retry value. **Decision axis**: correctness-of-semantics vs. parity-with-AskAsync.

3. **Q3 — Adapter-side idempotence contract.** Option (a) in Q2 requires both adapters (SK + MAF) to guarantee that a provider-side exception **before the first `CompletionUpdate` is yielded** leaves no observable state on the underlying HTTP client / connector — i.e. retrying by re-opening the enumerator is safe. Today both adapters behave this way incidentally (failures tend to surface as HttpRequestException or throttling exceptions synchronously inside the initial SSE handshake), but neither adapter *asserts* this contract. The spike needs to: audit SK's `IChatCompletionService.GetStreamingChatMessageContentsAsync` + MAF's `IChatClient.GetStreamingResponseAsync` call sites, write down the contract explicitly, and propose tests that assert it. **Decision axis**: how much adapter work does the pillar take on? Lean: tests + doc, not re-implementation.

4. **Q4 — Tool-call loop, budgets, and event emission under retry.** `StreamAsync` today emits one `TurnStarted` / `TurnCompleted` per **call**, not per **turn** inside the loop; aggregates tokens across turns; enforces `RunBudget` turn-by-turn. Retry semantics must not violate these invariants:
   - Polly's "one attempt" ≠ our "one turn". Token counts must not accumulate across failed attempts (provider error before first delta ⇒ nothing to count).
   - `TurnStarted` fires once at call entry, **not** per Polly attempt. Retry is invisible to the event bus except as a `TurnFailed` if retries exhaust.
   - Each streamed turn inside the tool-call loop is an **independent retry boundary** — retrying turn N+1 must not replay turns 1..N (working-history writes already happened).
   - `OnStreamDeltaAsync` / `OnStreamCompleteAsync` + output guardrails ordering: streaming-filter Complete → output guardrails → final session append → `TurnCompleted`. Stays unchanged.

   Lean: retry boundary lives **inside** the per-turn `while` loop, wrapping just the `streamingProvider.StreamAsync(...)` open + first-delta observation. Budget checks + tool dispatch sit outside the boundary and are never retried. **Decision axis**: which invariants stay load-bearing; which emission/budget surprises need codifying.

---

## Tasks (research + archetype exercises)

- [x] **Q1 — Filter surface.** Drafted three archetype sketches (rate-limit / request-rewriter / short-circuit cached) on each candidate shape. Scored on ergonomics / mental-model / PublicAPI cost / discoverability. Short-circuit cache discriminates: only shapes (a) and (b) can deliver it without buffering. **Outcome: (b) wins** — widen shipped `IStreamingAgentFilter` with additive DIM. Single type, three override points, mirrors `IAgentFilter`. See findings doc §Q1.
- [x] **Q2 — Retry semantics.** Pre-first-delta retry boundary prototyped as Phase 1 (retry: `ExecuteAsync` wrapping enumerator open + first `MoveNextAsync`) / Phase 2 (drain: `try { yield return ... } finally { dispose }`) split. `yield return`-inside-`try/catch` restriction respected. Cancellation propagation preserved via existing `OperationCanceledException` exclusion on default pipeline's `ShouldHandle`. 3-strategy comparison table in findings doc. **Outcome: (a) pre-first-delta only**; per-turn (not per-call) retry boundary.
- [x] **Q3 — Adapter idempotence audit.** Read `SkCompletionProvider.StreamAsync` (`:101-164`) + `MafCompletionProvider.StreamAsync` (`:131-209`). Both retry-safe by construction — preambles are pure local state, providers create fresh kernel-clone (SK) / `ChatClientAgent` (MAF) per call, no shared HTTP/connector state before first delta. Wrote normative contract clause for `IStreamingCompletionProvider` XML docs; proposed 2 positive + 1 negative test per adapter. **Zero adapter code changes needed** — tests alone pin the retry substrate.
- [x] **Q4 — Event/budget invariants.** State-machine diagram + 8-invariant table in findings doc. Key invariants: `TurnStarted` once-per-call (retry invisible to event bus); token accumulation per-turn not per-attempt; tool dispatch + `workingHistory` writes outside retry boundary; `OnStreamDeltaAsync` per-yielded-delta only on successful attempt; cancellation not retried. Edge cases covered: empty stream, cancellation during retry, filter-thrown exceptions (→ pillar must narrow default `ShouldHandle` to exclude agent-domain exceptions).
- [x] **Findings doc.** [`actor-agents-oss-v0.10-streaming-pipeline-findings.md`](./actor-agents-oss-v0.10-streaming-pipeline-findings.md) — Q1–Q4 synthesis + verdict (8 locked decisions, proposed 3-PR pillar shape, 1.5–2-day effort estimate).

---

## Exit criteria

- [x] All four questions answered with evidence (not opinion) — Q1 from archetype sketches ranked on a fixed rubric; Q2 from a retry-strategy comparison table + prototype pseudocode; Q3 from adapter code audit + proposed contract text; Q4 from a state-machine diagram + invariant table.
- [x] Three archetype consumer sketches on the filter surface shape in the findings doc, with a paragraph each on "this shape holds / breaks here".
- [x] Recommendation lands: **ready to write v0.10 pillar plan.** 8 decisions locked in the findings doc.

No public surface change. No package bumps. No tag.

---

## Progress log

- 2026-04-20 — spike plan created after design conversation. Four blocking questions scoped (filter surface shape, retry semantics, adapter idempotence contract, event/budget invariants under retry). Lean positions recorded per question going in; spike is to validate or overturn each.
- 2026-04-20 — Spike complete. All four leans held up. Q1: (b) widen shipped `IStreamingAgentFilter` — short-circuit cached-response archetype ruled out buffering adapter (c). Q2: pre-first-delta-only retry, per-turn boundary; `yield return` inside `try/catch` constraint respected via Phase 1 / Phase 2 split. Q3: both adapters retry-safe by construction — contract clause + tests only, zero code changes. Q4: 8 invariants (`TurnStarted` once-per-call; retry invisible to event bus; tokens per-turn not per-attempt; tool dispatch + workingHistory outside boundary; `OnStreamDeltaAsync` per-yielded-delta; cancellation not retried; filter-domain exceptions excluded from default `ShouldHandle`). Findings doc landed with 8 locked decisions + proposed 3-PR pillar shape (PR 1 Abstractions+Core; PR 2 adapter contract tests; PR 3 `v0.10.0-preview` cut). Effort estimate: 1.5–2 days. No new package. **Ready to write v0.10 pillar plan.**
