# v0.10.0-preview — Streaming filter + resilience pipeline pillar

Tactical plan for the streaming-pipeline pillar. Closes the [`extraction-research`](./actor-agents-oss-extraction-research.md) §7 backlog line: *"Streaming-filter pipeline + resilience-pipeline wrapping on streamed turns (the synchronous `IAgentFilter` chain + Polly pipeline are still bypassed on `StreamAsync`, same as v0.4; consumers needing filters stay on `AskAsync`). Needs a streaming-filter surface design."* Grounded in the spike findings: [`actor-agents-oss-v0.10-streaming-pipeline-findings.md`](./actor-agents-oss-v0.10-streaming-pipeline-findings.md). Parallel shape to [`actor-agents-oss-v0.9-graph-orchestration-pillar.md`](./actor-agents-oss-v0.9-graph-orchestration-pillar.md), but narrower — no new package, zero adapter code changes. Created 2026-04-20.

---

## Scope

**MVP boundary locked 2026-04-20** via the research spike. Eight decisions:

1. **Filter surface = widen shipped `IStreamingAgentFilter` with an additive DIM `InvokeAsync(request, next, ct) : IAsyncEnumerable<CompletionUpdate>`.** Single type, three override points (around-provider / per-delta / end-of-stream), mirrors `IAgentFilter`'s role on the non-streaming side. Short-circuit cached-response archetype discriminated against the buffering alternative — that option cannot yield cached chunks incrementally.
2. **Filter composition = agent wraps the chain around the provider call.** Same lazy right-to-left build-up as `StatefulAiAgent.InvokeThroughFiltersAsync`, adapted to `IAsyncEnumerable<CompletionUpdate>`. The per-delta hook (`OnStreamDeltaAsync`) + terminal hook (`OnStreamCompleteAsync`) remain **agent-driven** — the agent iterates the resulting stream and fires them between `InvokeAsync`'s yield and the caller. Preserves the "the agent owns the iteration" invariant that makes event emission, budget enforcement, and guardrail ordering predictable.
3. **Retry semantics = pre-first-delta-only.** Polly wraps the enumerator-open + first `MoveNextAsync` on the streaming-filter chain. Once the first delta is yielded to the caller, retries stop — "yielded = committed." Transient connect / 429 / DNS failures recover; mid-stream failures surface to the caller unretried.
4. **Retry boundary scope = per-turn, inside the tool-call loop.** Each streamed turn in the tool-calling loop is an independent retry boundary. Retries for turn N+1 never replay turn N's deltas, dispatches, or `workingHistory` writes.
5. **Adapter contract (not code changes).** Normative clause on `IStreamingCompletionProvider.StreamAsync` XML docs: *"Implementations must guarantee that any exception before the first `CompletionUpdate` is yielded leaves no observable side-effect on shared state. Exceptions after the first delta are not retryable."* Both shipped adapters (SK + MAF) satisfy this by construction today — kernel-clone (SK) / fresh `ChatClientAgent` (MAF) per call, no cross-call connector state. 6 tests pin the behaviour.
6. **Retry-predicate discipline.** Default streaming pipeline's `ShouldHandle` excludes `OperationCanceledException` (matches the existing non-streaming default) **and** agent-domain exceptions (`AgentGuardrailDeniedException`, `AgentBudgetExceededException`, `AgentInterruptedException`). One internal `IsFilterDomainException` helper in Core drives both predicates.
7. **Event invariants stay load-bearing.** `TurnStarted` / `TurnCompleted` fire once per call (unchanged). Retry is invisible to the event bus except as `TurnFailed` when retries exhaust. Token counts aggregate per-turn, never per-attempt. `OnStreamDeltaAsync` fires per yielded delta; `OnStreamCompleteAsync` fires once per final-answer turn before output guardrails.
8. **Separate streaming resilience pipeline knob.** `StatefulAgentOptions.StreamingResiliencePipeline` is a sibling to `StatefulAgentOptions.ResiliencePipeline`. Null ⇒ the agent's internal streaming default (same retry cadence as the non-streaming default, narrower `ShouldHandle`). Consumers who want identical-behaviour retry on both paths assign the same pipeline instance; consumers who want different budgets assign two.

### Semantic projection chosen

**Streaming turn = retry-wrapped provider call + agent-driven delta iteration + per-turn invariants.** Filter chain wraps the provider call; Polly wraps the first-delta observation window; per-delta and end-of-stream hooks remain agent-driven; tool dispatch + working-history writes sit outside the retry boundary so retries are idempotent from the provider's perspective.

### Explicitly deferred to post-v0.10

- **Per-attempt telemetry propagation.** SK and MAF emit their own internal `Activity` on each provider attempt; retry surfaces as two internal spans inside one outer `chat` span. Not changing our span model in v0.10 — consumers who want per-attempt visibility can wire their own inner `ActivitySource` or inspect `HttpClient`-level spans.
- **Streaming journal replay.** v0.5's `IAgentJournal` stays tool-call-granular; streamed-delta replay is not in scope (see research doc §7 "Temporal parity" roadmap).
- **Orleans streaming passthrough to remote hosts.** `StreamAsync` on the concrete `StatefulAiAgent` is the surface today; it's not on the `IAiAgent` contract and not proxied through `OrleansAiAgentProxy`. Unchanged in v0.10.
- **Buffer-everything fallback.** Consumers who want `IAgentFilter`-shaped request→response semantics on streaming stay on `AskAsync` as documented.
- **Retry budget separate from turn budget.** Polly's retry attempts inside one turn don't count toward `RunBudget.MaxTurns`. If consumers want "no more than N retries across the whole run," they assign a pipeline with a run-scoped state. Not modelled as a first-class budget in v0.10.

---

## Design questions — resolved

| # | Question | Decision | Reasoning |
|---|---|---|---|
| 1 | Filter surface — one type or two? | Widen `IStreamingAgentFilter` (one type, DIM) | Single registration; mirrors `IAgentFilter`'s role on the non-streaming side; short-circuit cache discriminates against the buffering alternative |
| 2 | Filter composition — agent-driven iteration or filter-driven? | Agent-driven (unchanged) | Preserves event/budget/guardrail ordering; filters implement only the override point they need |
| 3 | Retry scope on the stream | Pre-first-delta-only | Transient failures recover; yielded = committed; no partial-output replay |
| 4 | Retry boundary in the tool-call loop | Per-turn | Each streamed turn is independent; retries don't replay prior turns |
| 5 | Adapter contract | Documented + tested, zero code changes | Both SK + MAF are retry-safe by construction today |
| 6 | Retry predicate discipline | Exclude OCE + agent-domain exceptions | Matches existing non-streaming default; prevents filter-thrown exceptions from retrying |
| 7 | Method naming on the DIM | `InvokeAsync` | Matches `IAgentFilter`'s around-provider method name; visual co-location on the same type is OK given the differing signatures |
| 8 | Streaming resilience pipeline = same as non-streaming or separate knob? | Separate knob on `StatefulAgentOptions`, same default cadence | Consumers who want parity assign the same instance; consumers who want separate budgets get it |

### Open questions (low-stakes, resolve during impl)

1. **Empty-stream edge case.** Provider yields zero deltas (first `MoveNextAsync` → `false` without throwing). Today's code falls through to the "no tool calls ⇒ finalise" path with an empty accumulator. Retry boundary does not change this: `streamLive = false` after Phase 1, skip Phase 2 drain, same finalisation. Add an explicit test — silent today.
2. **Streaming-filter exception classification.** If a filter throws before calling `next`, Polly's `ShouldHandle` sees the exception at the `ExecuteAsync` boundary. Decision #6 excludes known agent-domain exceptions from retry; is there a broader category we should also exclude (filter-authored custom exceptions)? Lean: no — filter authors who want non-retryable errors should use one of the existing agent-domain types or wrap into `AgentGuardrailDeniedException`. Document it.
3. **Polly pipeline disposal.** Today `StatefulAiAgent._pipeline` is a static default shared across all instances. The streaming variant can use the same pattern — one static default, consumers override per-instance via options. No per-instance disposal concern.
4. **`OnStreamDeltaAsync` firing order with short-circuit cache.** If a filter short-circuits via `yield break` after yielding cached chunks, does the agent still call `OnStreamDeltaAsync` on those cached chunks? Lean: yes — the agent iterates whatever the chain yields, regardless of origin. Short-circuit filters that also override `OnStreamDeltaAsync` get their own per-delta hook called on their own yielded chunks. Documented behaviour.
5. **First-delta buffering cost.** Phase 1 captures the first delta into a local before entering Phase 2's drain loop. The first delta's memory lifetime is one stack frame — negligible. No streaming degradation.
6. **`StreamingResiliencePipeline` default — wire it in PR 1 or PR 3?** Lean: PR 1 ships the knob + default; PR 3 just bumps the version.

---

## No new packages

Package-count stays at **22** (same as v0.9). All v0.10 work lives as extensions inside existing packages.

Extended packages (zero breaking changes on existing surface):
- **`Vais.Agents.Abstractions`** — DIM addition to `IStreamingAgentFilter` + normative contract clause on `IStreamingCompletionProvider` XML docs.
- **`Vais.Agents.Core`** — `StatefulAiAgent.StreamAsync` per-turn loop refactor (Phase 1 retry boundary + Phase 2 drain + filter-chain wrap); `StatefulAgentOptions.StreamingResiliencePipeline`; internal `IsFilterDomainException` helper + `BuildDefaultStreamingPipeline`.
- **`Vais.Agents.Ai.SemanticKernel`** — no code changes; tests added for idempotence contract.
- **`Vais.Agents.Ai.MicrosoftAgentFramework`** — no code changes; tests added for idempotence contract.

---

## Delivery

### PR 1 — Abstractions DIM + Core retry loop + streaming-filter chain

**Packages**: `Vais.Agents.Abstractions` (extend) + `Vais.Agents.Core` (extend).

Tasks:

- [x] Widen `IStreamingAgentFilter` with additive DIM `InvokeAsync(request, next, ct) : IAsyncEnumerable<CompletionUpdate>`. Default body delegates straight to `next(request, ct)`. XML docs on the type explain the three override points (when to pick which).
- [x] `IStreamingCompletionProvider` XML-doc update — normative idempotence contract clause. No method signature change.
- [x] `StatefulAgentOptions.StreamingResiliencePipeline` (new property, `ResiliencePipeline?`, default null ⇒ agent's internal default). XML docs explain the separation from `ResiliencePipeline`.
- [x] Internal `IsFilterDomainException(Exception) : bool` helper in Core — returns true for `AgentGuardrailDeniedException`, `AgentBudgetExceededException`, `AgentInterruptedException`, `OperationCanceledException`. Shared by both the existing non-streaming `BuildDefaultPipeline` (replace the inline `ex is not OperationCanceledException` predicate) and the new `BuildDefaultStreamingPipeline`.
- [x] `StatefulAiAgent.BuildDefaultStreamingPipeline()` — private static, mirrors `BuildDefaultPipeline()`'s retry cadence (2 retries, exponential backoff, jitter) but `ShouldHandle` uses the new helper.
- [x] `StatefulAiAgent.InvokeThroughStreamingFiltersAsync(request, ct) : IAsyncEnumerable<CompletionUpdate>` — lazy right-to-left chain build, terminal step calls `_streamingProvider.StreamAsync(request, ct)`. Parallel shape to `InvokeThroughFiltersAsync` but returns an async enumerable.
- [x] `StatefulAiAgent.StreamAsync` per-turn loop refactor:
   - Phase 1 (retry boundary): `_streamingPipeline.ExecuteAsync(async ct => { dispose prior enumerator if any; open new enumerator via filter chain; first MoveNext; capture firstUpdate; set streamLive })` — retries transient failures pre-first-delta. `try/catch` around `ExecuteAsync`: `OperationCanceledException` rethrows; other exceptions land on `failure` and break.
   - Phase 2 (drain): `try { yield return ProcessDelta(firstUpdate); while MoveNext { yield return ProcessDelta(current) } } finally { dispose enumerator }`. Mid-stream exceptions bubble into `failure` via the existing outer catch and end the turn. `yield return`-inside-`try/catch` restriction respected — Phase 2 uses `try/finally` only.
   - `ProcessDelta(update)` applies the existing per-filter `OnStreamDeltaAsync` chain (unchanged loop from today), accumulates text + tokens + modelId + tool-calls, then returns the (possibly transformed) delta for the caller.
- [x] Hold-point check for invariants (Q4 state machine): `TurnStarted` emission position, token aggregation position, `OnStreamCompleteAsync` position, output-guardrail position, tool-dispatch position — all unchanged from the current `StreamAsync`. The only new control flow sits inside the per-turn while loop.
- [x] `PublicAPI.Unshipped.txt` updates — Abstractions (1 new DIM on `IStreamingAgentFilter`); Core (`StatefulAgentOptions.StreamingResiliencePipeline` get/set; no public changes on `StatefulAiAgent` — the retry work is internal).
- [x] Tests — 12+ new in `Vais.Agents.Core.Tests/StreamingFilterPipelineTests.cs`:
   - (1) Around-provider filter short-circuits via `yield break`, returns cached chunks — caller sees all cached chunks, `OnStreamDeltaAsync` fires on each, `next` never invoked.
   - (2) Rate-limit around-provider filter denies — caller sees `RateLimitExceededException`, no deltas yielded.
   - (3) Request-rewriter around-provider filter mutates `CompletionRequest.MaxTokens` — provider fake asserts it saw the rewritten value.
   - (4) Chain of 2 around-provider filters composes correctly (both filter bodies run, in order).
   - (5) Around-provider filter that also implements `OnStreamDeltaAsync` — both hooks fire, in documented order.
   - (6) Retry on transient failure pre-first-delta succeeds — provider fake throws `HttpRequestException` on first `StreamAsync`, yields 3 deltas on second call; caller sees all 3 deltas; provider invoked twice.
   - (7) Retry on first-`MoveNextAsync` failure pre-first-delta succeeds — same expected behaviour.
   - (8) Post-first-delta failure is not retried — caller sees delta #1 + `failure` surfaces on delta #2's `MoveNextAsync`; provider invoked once; `TurnFailed` emitted.
   - (9) `OperationCanceledException` on `cancellationToken` firing pre-first-delta is not retried — propagates immediately.
   - (10) `AgentGuardrailDeniedException` thrown by a filter before `next` is called is not retried — propagates immediately; matches `IsFilterDomainException` predicate.
   - (11) Empty stream (zero deltas yielded, first `MoveNextAsync` returns false) finalises the turn — `OnStreamCompleteAsync` fires with empty-text response; session appended with empty assistant turn; `TurnCompleted` emitted.
   - (12) Tool-call loop retry boundary is per-turn — provider fake: turn 1 yields deltas + tool call; turn 2 first attempt throws; turn 2 second attempt yields deltas + final answer. Caller sees: turn-1 deltas, turn-1 tool dispatched (ONCE — dispatcher assertion), turn-2 deltas from the retry attempt, final answer.

### PR 2 — Adapter contract tests

**Packages**: `Vais.Agents.Ai.SemanticKernel` (tests only) + `Vais.Agents.Ai.MicrosoftAgentFramework` (tests only).

No production code changes. The idempotence contract is already satisfied by both adapters (see findings doc §Q3).

Tasks:

- [x] SK adapter — 3 new tests in `Vais.Agents.ParityTests/StreamingIdempotenceParityTests.cs` (the project already hosts both-stack fakes + references both adapters; no dedicated SK-only test project exists, so the parity project is the natural home):
   - (1) **Preamble failure retries**: fake `IChatCompletionService` whose `GetStreamingChatMessageContentsAsync` throws `HttpRequestException` on first invocation, returns 3 chunks on second; assert SK adapter's `StreamAsync` yields all 3 deltas when the outer retry replays; assert the fake was invoked twice.
   - (2) **First-`MoveNextAsync` failure retries**: fake that returns an enumerable whose first `MoveNextAsync()` throws on the first iteration and yields 3 chunks on the second iteration; same asserts.
   - (3) **Post-first-delta failure is not retried**: fake yields chunk 1, throws `IOException` on chunk 2's `MoveNextAsync`; assert agent yielded chunk 1 to caller, surfaced failure; fake invoked once.
- [x] MAF adapter — 3 new tests in the same file using a `FlakyChatClient` test double:
   - (1) **Preamble failure retries**: fake `IChatClient` whose `GetStreamingResponseAsync` throws on first call, returns 3 updates on second.
   - (2) **First-`MoveNextAsync` failure retries**: fake iterator first attempt throws on first `MoveNextAsync`, second attempt yields 3 updates.
   - (3) **Post-first-delta failure is not retried**: first update yields, second update throws.
- [x] `Vais.Agents.ParityTests` — 1 cross-stack parity test: same retry scenario (preamble failure + second-attempt success) runs cleanly across the SK fake and the MAF fake with identical observable behaviour (same yielded deltas in order, same attempt count).
- [x] No PublicAPI changes — contract text lives in XML docs landed in PR 1.
- [x] No adapter code changes — verified by `git diff --stat src/Vais.Agents.Ai.SemanticKernel src/Vais.Agents.Ai.MicrosoftAgentFramework` returning empty.

### PR 3 — `v0.10.0-preview` cut

**Packages**: all 22.

Tasks:

- [x] API freeze: `Unshipped` → `Shipped` across the two touched packages (`Vais.Agents.Abstractions` — 1 DIM entry; `Vais.Agents.Core` — 2 options getter/init entries; zero on the adapter packages since they didn't change). Other 20 packages shipped unchanged since `v0.9.0-preview`.
- [x] Pack: `dotnet pack -c Release -p:VersionPrefix=0.10.0 -p:VersionSuffix=preview -o artifacts/packages` → 22 `.nupkg` + 22 `.snupkg`, all present in `oss/agentic/artifacts/packages/`.
- [x] Smoketest: refreshed package refs to `0.10.0-preview`; added a streaming-pipeline probe segment that (a) registers a fake streaming provider, (b) registers a custom `IStreamingAgentFilter` implementing the around-provider `InvokeAsync` to rewrite `MaxTokens`, (c) runs `agent.StreamAsync("ping")` and collects deltas, (d) asserts the filter's around-provider body ran + the rewritten request was observed + deltas landed + assistant turn appended. Probe line prints `filter-around-invoked=True delta-hook-count=2 request-rewrite-max-tokens=77 deltas-yielded=[smoke ,reply] assistant-turn-appended=True`. Final line updated to `"All twenty-two Vais.Agents.* 0.10.0-preview packages consumed cleanly from a plain .NET 9 console app."`
- [x] Tag: annotated `v0.10.0-preview` created on OSS repo `main` at commit `b28d80d` (API freeze). Not pushed.
- [x] Milestone log entry in [`actor-agents-oss-milestone-log.md`](./actor-agents-oss-milestone-log.md).
- [x] Research doc §7 update — "Streaming-filter pipeline + resilience-pipeline wrapping on streamed turns" backlog line struck through, pointed at this pillar + findings.
- [x] `StatefulAiAgent.StreamAsync` XML doc — "V0.4.1 scope gaps" paragraph replaced with the v0.10 behaviour description (landed in PR 1).

---

## Exit criteria

- [x] All 3 PRs on OSS repo `main` (not pushed), landed as the two-commit pattern used in v0.7/v0.8/v0.9 (feat `f2efddc` for PRs 1-2; chore `b28d80d` for PR 3 API freeze).
- [x] Zero new packages; extensions to 2 production + 2 test projects pack cleanly at `0.10.0-preview` — 22 `.nupkg` + 22 `.snupkg` in `artifacts/packages/`.
- [x] Full non-container test suite green: 523 tests (490 v0.9 baseline + 12 streaming-pipeline + 7 idempotence + 14 Redis/Postgres/CrossHost integration tests counted now).
- [x] Smoketest probes the streaming-filter around-provider surface — custom filter runs end-to-end, rewrites request, receives deltas — from a fresh .NET 9 console project with only NuGet references.
- [x] `v0.10.0-preview` tag created on the freeze commit.
- [ ] **Acceptance demo (manual)**: a real OpenAI/Ollama-backed `SkCompletionProvider` hits a fake network-fault injector that drops the first SSE handshake; the agent's `StreamAsync` recovers via retry, yields the full response to the caller, and emits one `TurnCompleted`. **Not yet run** — the unit-test parity scenarios in PR 2 (`Sk_Preamble_Failure_Retries_Safely` + `Maf_Preamble_Failure_Retries_Safely` + `Both_Adapters_Produce_Equivalent_Streams_After_Retry`) are the closest automated equivalent and they're green. Run manually against the smoketest host with a mock-HTTP injector when time allows.

---

## Decisions locked (from the spike + research walkthrough 2026-04-20)

- **Widen `IStreamingAgentFilter` with DIM `InvokeAsync(req, next, ct) : IAsyncEnumerable<CompletionUpdate>`.** Single type, three override points.
- **Agent-driven delta iteration stays.** Around-provider filters wrap the provider call; `OnStreamDeltaAsync` + `OnStreamCompleteAsync` remain agent-driven between the filter's yield and the caller's yield.
- **Pre-first-delta-only retry.** Per-turn boundary; each streamed turn in the tool-call loop is independent.
- **`IStreamingCompletionProvider` normative idempotence contract.** Documented in XML; both SK + MAF adapters already satisfy it by construction; zero adapter code changes.
- **Retry predicate excludes OCE + agent-domain exceptions.** Shared `IsFilterDomainException` helper in Core, used by both the existing non-streaming pipeline and the new streaming pipeline.
- **`StatefulAgentOptions.StreamingResiliencePipeline`** — sibling to `ResiliencePipeline`; null ⇒ agent's internal streaming default.
- **Event/budget invariants** (8-item invariant table in findings doc) **preserved**: `TurnStarted` once-per-call, retry invisible to event bus, tokens per-turn not per-attempt, tool dispatch outside retry boundary, `OnStreamDeltaAsync` per yielded delta, cancellation not retried.
- **`yield return`-in-`try/catch` restriction respected** via the Phase 1 / Phase 2 split in the per-turn while loop.

---

## Progress log

- 2026-04-20 — plan created after the streaming-pipeline spike closed. 8 decisions locked from the spike's verdict; 3 PRs scoped; 6 open questions flagged for impl. Package count stays at 22 (no new package). Target effort: 1.5–2 days focused work (PR 1 retry state machine + 12 tests is the bulk; PR 2 is parity boilerplate; PR 3 is the cut/pack rote). **Pending**: start on PR 1 (Abstractions DIM + Core retry loop + streaming-filter chain).
- 2026-04-20 — PR 1 landed on `033-logging-improvement-read`. `Vais.Agents.Abstractions` extended with 1 new DIM on `IStreamingAgentFilter.InvokeAsync` (around-provider hook, default passes straight to `next`) + normative idempotence contract clause on `IStreamingCompletionProvider.StreamAsync` XML docs. `Vais.Agents.Core` extended with `StatefulAgentOptions.StreamingResiliencePipeline` + 3 new private/internal members on `StatefulAiAgent` (`_streamingPipeline` field, `_defaultStreamingPipeline` static, `InvokeThroughStreamingFilters` helper, `BuildDefaultStreamingPipeline` static, `IsFilterDomainException` internal static). `StatefulAiAgent.StreamAsync` per-turn loop refactored: Phase 1 retry boundary (`ExecuteAsync` wrapping enumerator-open + first `MoveNextAsync` through the streaming-filter chain) → Phase 2 drain (try/finally with yield returns, mid-stream failures bubble via local `failure`). Existing non-streaming `BuildDefaultPipeline` rewired to use the same `IsFilterDomainException` predicate (was inline OCE check). 12 new tests in `Vais.Agents.Core.Tests/StreamingFilterPipelineTests.cs` exercising short-circuit cache filter, deny-via-filter non-retry, request-rewriter, 2-filter composition order, around+delta-hook coexistence, pre-first-delta retry (sync-throw + first-MoveNextAsync flavours), post-first-delta non-retry, cancellation non-retry, guardrail-denial non-retry, empty-stream finalisation, per-turn retry boundary inside tool-call loop. Full non-container suite green: 318/318 Core (was 306, +12), 516 across the whole solution (490 v0.9 baseline + 12 new + 14 Redis/Postgres/CrossHost integration tests I was previously counting as container-only). **Shape adjustments during impl**: (1) the retry boundary's `ExecuteAsync` callback owns the lifecycle of the "next-attempt enumerator" — an outer-scope `enumerator` variable is promoted from the inner `e` only on successful first-`MoveNextAsync`; empty-stream attempts dispose `e` immediately, so retry semantics are clean across partial-producer scenarios. (2) The inner MoveNextAsync loop in Phase 2 uses a `CompletionUpdate? currentUpdate` variable threaded through the loop body instead of the pre-refactor pattern of `while(true) { fetch; process; }` — keeps the "process firstUpdate first, then advance" semantics without duplicating the process block. (3) Open-question #2 resolved: filter-domain exception classification stays narrow — just the four well-known agent-domain types and `OperationCanceledException`. Consumers wanting non-retryable custom exceptions wrap into `AgentGuardrailDeniedException` (documented on `IStreamingAgentFilter` XML docs). **Pending**: PR 2 (adapter idempotence contract tests).
- 2026-04-20 — PR 2 landed on `033-logging-improvement-read`. **Zero production code changes** under `src/Vais.Agents.Ai.SemanticKernel` + `src/Vais.Agents.Ai.MicrosoftAgentFramework` (verified by `git diff --stat` returning empty on those dirs). 7 new tests in `tests/Vais.Agents.ParityTests/StreamingIdempotenceParityTests.cs` — 3 per adapter (preamble-failure retry / first-MoveNextAsync-failure retry / post-first-delta-failure non-retry) + 1 cross-stack parity test asserting identical observable behaviour on the preamble-failure-then-success scenario. New `FlakyChatCompletionService` + `FlakyChatClient` test doubles use a per-attempt behaviour queue so each attempt gets its own factory (sync throw, enumerable-throws-on-first-MoveNextAsync, yield-then-throw). `Vais.Agents.ParityTests.csproj` gained `Microsoft.Extensions.Resilience` package ref for zero-delay Polly pipelines in tests. Full non-container suite green: 523 across the whole solution (516 after PR 1, +7 new). **Shape adjustment from the pillar plan**: the plan named `Vais.Agents.Ai.SemanticKernel.Tests` + `Vais.Agents.Ai.MicrosoftAgentFramework.Tests` as the test-file destinations, but those projects don't exist — the OSS layout uses `Vais.Agents.ParityTests` as the shared home for both-adapter tests, which is the right place for contract tests anyway (references both adapters, already hosts the scripted fakes for the existing streaming-parity tests). Single test file hosts the 6 per-adapter tests + 1 cross-stack parity test; the per-adapter intent (3 tests per stack) is preserved via `[Fact]` naming. **Pending**: PR 3 (v0.10.0-preview cut — API freeze, pack 22 packages, smoketest extension, tag).
- 2026-04-20 — PR 3 landed on OSS `main`. Two commits: `f2efddc feat(streaming): filter + resilience pipeline on StreamAsync (v0.10 PRs 1-2)` (9 files, +1234 −80) + `b28d80d chore: API freeze for v0.10.0-preview — promote Unshipped -> Shipped` (4 files, 3 Unshipped entries promoted across 2 packages — Abstractions +1, Core +2). Annotated `v0.10.0-preview` tag created on `b28d80d` (not pushed). 22 `.nupkg` + 22 `.snupkg` packed at `0.10.0-preview` into `artifacts/packages/`. Smoketest refreshed to `0.10.0-preview` with new streaming-pipeline probe segment that exercises a custom around-provider filter rewriting `MaxTokens` + accumulator + assistant-turn-append assertion; ran clean. Probe line prints `filter-around-invoked=True delta-hook-count=2 request-rewrite-max-tokens=77 deltas-yielded=[smoke ,reply] assistant-turn-appended=True`. Final line: `"All twenty-two Vais.Agents.* 0.10.0-preview packages consumed cleanly from a plain .NET 9 console app."` Milestone log entry appended (`actor-agents-oss-milestone-log.md`). Research doc §7 "Streaming-filter pipeline + resilience-pipeline wrapping on streamed turns" backlog line struck through and pointed at this pillar + findings doc. **Pillar closed.** Only follow-up remaining: the manual acceptance demo (a real OpenAI/Ollama-backed `SkCompletionProvider` against a mock-HTTP injector that drops the first SSE handshake) — unit tests are the closest automated equivalent and they're green.
