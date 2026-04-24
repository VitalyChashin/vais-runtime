# v0.10 Streaming pipeline — spike findings

Synthesis of the research spike scoped in [`actor-agents-oss-v0.10-streaming-pipeline-spike.md`](./actor-agents-oss-v0.10-streaming-pipeline-spike.md). Answers Q1–Q4 with evidence, not opinion. Landing verdict at the bottom.

Created 2026-04-20. **Status**: complete. Q1 (archetype sketches), Q2 (retry-strategy comparison), Q4 (state machine) synthesised locally from the shipped code. Q3 (adapter audit) from direct read of `SkCompletionProvider.StreamAsync` + `MafCompletionProvider.StreamAsync`.

---

## Q1 — Filter surface shape

### Rubric

1. **Rate-limit filter** — decide-before-stream, pass through on approve, throw on deny. No delta access needed.
2. **Request-rewriter filter** — mutate `CompletionRequest` before calling `next`. No delta access.
3. **Short-circuit cached-response filter** — skip `next` and yield pre-recorded deltas from a cache. **This is the discriminator.**

### Three candidate shapes

**(a) New `IStreamingProviderFilter` standalone.**

```csharp
public interface IStreamingProviderFilter
{
    IAsyncEnumerable<CompletionUpdate> InvokeAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
        CancellationToken cancellationToken);
}
```

Registration: `StatefulAgentOptions.StreamingProviderFilters` (new list). `IStreamingAgentFilter` stays for per-delta transforms. Two types, two registrations.

**(b) Widen shipped `IStreamingAgentFilter` with a third DIM method.**

```csharp
public interface IStreamingAgentFilter
{
    // EXISTING (shipped v0.3 / M3e-2):
    ValueTask<CompletionUpdate> OnStreamDeltaAsync(CompletionUpdate update, CancellationToken ct = default)
        => ValueTask.FromResult(update);
    ValueTask OnStreamCompleteAsync(CompletionResponse final, CancellationToken ct = default)
        => ValueTask.CompletedTask;

    // NEW (additive DIM; default delegates straight to next):
    IAsyncEnumerable<CompletionUpdate> InvokeAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
        CancellationToken cancellationToken)
        => next(request, cancellationToken);
}
```

Composition — the agent wraps the streaming-filter chain around the provider (same pattern as `InvokeThroughFiltersAsync` for `AskAsync`), then iterates the resulting stream and fires `OnStreamDeltaAsync` on every delta before yielding to the caller:

```
agent.StreamAsync
  → build chain: f1.InvokeAsync(req, λ→f2.InvokeAsync(req, λ→...→provider))
  → iterate the resulting IAsyncEnumerable<CompletionUpdate>
    → agent calls OnStreamDeltaAsync on every filter, in order, per delta
    → agent yields the (possibly transformed) delta to the caller
  → at end: agent calls OnStreamCompleteAsync on every filter, in order
```

`OnStreamDeltaAsync` + `OnStreamCompleteAsync` stay agent-driven (unchanged). `InvokeAsync` is filter-driven (filter owns whether to call `next` at all — hence short-circuit works).

Registration: `StatefulAgentOptions.StreamingFilters` (existing list). One type, one registration.

**(c) Default-adapt `IAgentFilter` onto streaming by buffering.**

Agent buffers the provider stream into a full `CompletionResponse`, calls the `IAgentFilter` chain against that, re-yields the response text as a single synthetic delta.

### Archetype sketches

**Rate-limit — any shape works:**

```csharp
// (a) / (b): same body, different interface
public async IAsyncEnumerable<CompletionUpdate> InvokeAsync(
    CompletionRequest request,
    Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
    [EnumeratorCancellation] CancellationToken ct)
{
    if (!await _limiter.TryAcquireAsync(ct).ConfigureAwait(false))
        throw new RateLimitExceededException();
    await foreach (var u in next(request, ct).ConfigureAwait(false))
        yield return u;
}
```

**Request-rewriter — any shape works:**

```csharp
public async IAsyncEnumerable<CompletionUpdate> InvokeAsync(
    CompletionRequest request,
    Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
    [EnumeratorCancellation] CancellationToken ct)
{
    var rewritten = request with { MaxTokens = Math.Min(request.MaxTokens ?? int.MaxValue, _cap) };
    await foreach (var u in next(rewritten, ct).ConfigureAwait(false))
        yield return u;
}
```

**Short-circuit cached response — discriminator:**

```csharp
// (a) / (b):
public async IAsyncEnumerable<CompletionUpdate> InvokeAsync(
    CompletionRequest request,
    Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
    [EnumeratorCancellation] CancellationToken ct)
{
    if (_cache.TryGet(request, out var cached))
    {
        foreach (var chunk in cached.Chunks)
        {
            ct.ThrowIfCancellationRequested();
            yield return new CompletionUpdate(chunk, cached.ModelId);
            await Task.Yield();
        }
        yield break; // never call next
    }
    await foreach (var u in next(request, ct).ConfigureAwait(false))
        yield return u;
}
```

On **(c)**: impossible. Buffering adapter has no way to yield cached chunks incrementally to the caller — the `IAgentFilter` returns a single `CompletionResponse`. Short-circuit from cache loses streaming.

### Scoring

| shape                                           | rate-limit | request-rewrite | short-circuit cache  | PublicAPI cost              | consumer mental model                      |
|-------------------------------------------------|------------|-----------------|----------------------|-----------------------------|--------------------------------------------|
| (a) standalone `IStreamingProviderFilter`       | OK         | OK              | OK                   | +1 type, +1 options list    | "two things to register; when which?"      |
| (b) widen `IStreamingAgentFilter` (DIM)         | OK         | OK              | OK                   | +1 DIM method, additive     | "same type, three override points"         |
| (c) default-adapt via buffer                    | OK         | OK (req only)   | **broken** (no stream) | zero new types              | "works until it doesn't — buffering surprise" |

### Decision (Q1): **(b) — widen `IStreamingAgentFilter`**

Single type, single registration, three override points that mirror `IAgentFilter`'s around-provider role + the existing per-delta / terminal hooks. Short-circuit caching and request-rewriting both work cleanly. DIM keeps it non-breaking on a shipped interface (PublicAPI analyzer wants a new Unshipped entry for the method but no `*REMOVED*` churn on existing members).

**Sub-decision (composition)**: `InvokeAsync` wraps the provider call; the agent fires `OnStreamDeltaAsync` and `OnStreamCompleteAsync` on every filter *after* `InvokeAsync` yields to the agent. Filter authors override whichever knob they need; they don't wire the per-delta hook by hand. Matches today's driving model — preserves the "the agent owns the iteration" invariant that makes event-bus emission, budget enforcement, and guardrail ordering predictable.

**Trade-off accepted**: adding a DIM to a shipped interface is a small-but-real ergonomic cost — consumers who implemented `IStreamingAgentFilter` pre-v0.10 get a silent no-op override for `InvokeAsync`. That's a feature (opt-in), not a break. Doc update on the type + a changelog note suffice.

---

## Q2 — Retry semantics

### Comparison table

| strategy                         | retry success                         | streaming preserved             | impl complexity                                      | consumer surprise                                    |
|----------------------------------|---------------------------------------|---------------------------------|------------------------------------------------------|------------------------------------------------------|
| (a) pre-first-delta only         | transient conn / 429 / DNS → recover  | yes — commits once producing    | medium — retry the enumerator-open + first MoveNext | low — "yielded = committed"                          |
| (b) full buffered retry          | everything retries                    | **no** — degenerates to AskAsync | low — wrap whole drain in pipeline                  | high — why register streaming if buffered?           |
| (c) no retry                     | nothing retries                       | yes                             | trivial                                              | medium — asymmetric with AskAsync, silent gap stays  |

### Decision (Q2): **(a) pre-first-delta only**

Preserves streaming while recovering the 80% retry value (connection setup is where almost all transient faults surface). Matches the correct mental model: yielded deltas are committed; the provider's "open + first byte" handshake is the one retryable window.

### Prototype — retry boundary inside `StreamAsync`'s per-turn loop

Pseudocode replacing the current direct `streamingProvider.StreamAsync(...)` call (`StatefulAiAgent.cs:445`). The new streaming-filter chain `InvokeThroughStreamingFiltersAsync` wraps the provider call; the Polly pipeline wraps the enumerator-open + first MoveNext:

```csharp
// Per-turn in StreamAsync:
IAsyncEnumerator<CompletionUpdate>? enumerator = null;
CompletionUpdate? firstUpdate = null;
bool streamLive = false;

// Phase 1: retry boundary — pipeline retries the open + first MoveNext.
// ShouldHandle predicate on default pipeline already excludes OperationCanceledException.
try
{
    await _pipeline.ExecuteAsync(async attemptCt =>
    {
        if (enumerator is not null)
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
            enumerator = null;
        }
        var stream = InvokeThroughStreamingFilters(request, attemptCt);   // Q1 chain
        enumerator = stream.GetAsyncEnumerator(attemptCt);
        if (await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            firstUpdate = enumerator.Current;
            streamLive = true;
        }
    }, cancellationToken).ConfigureAwait(false);
}
catch (OperationCanceledException) { throw; }
catch (Exception ex) { failure = ex; break; }

// Phase 2: drain outside retry boundary. yield return cannot sit inside try/catch
// so the drain is a plain try/finally; exceptions bubble and land on the outer
// `failure` path via the same catch the current code uses.
if (streamLive)
{
    try
    {
        // first delta fires the per-delta chain + the accumulator + yield
        yield return ProcessDelta(firstUpdate!);
        while (await enumerator!.MoveNextAsync().ConfigureAwait(false))
        {
            yield return ProcessDelta(enumerator.Current);
        }
    }
    finally
    {
        if (enumerator is not null) await enumerator.DisposeAsync().ConfigureAwait(false);
    }
}
```

### C# restriction (known from v0.9)

`yield return` cannot sit inside a `try { ... } catch`. Only `try { ... } finally` is legal. The prototype above respects this: retry boundary (Phase 1) is `try { await ... } catch { break; }` with no yield; drain (Phase 2) is `try { yield return ... } finally { dispose; }` with only `finally`. Mid-stream exceptions after the first delta bubble to the outer `while(!loopDone)`'s existing exception path (same as today).

### Sub-decision: per-turn retry boundary, not per-call

Each streamed turn in the tool-call loop is an **independent** retry boundary. If turn N+1 opens its provider stream and fails, Polly retries the N+1 open only — turns 1..N's deltas are already yielded, tool calls already dispatched, `workingHistory` already written. Turn N+1's retry sees the same `workingHistory` and is therefore idempotent from the provider's perspective.

---

## Q3 — Adapter idempotence contract

### Audit findings

Both adapters are `async IAsyncEnumerable<CompletionUpdate>` with `[EnumeratorCancellation]`, and both are retry-safe by construction pre-first-delta:

**SK (`SkCompletionProvider.StreamAsync`, lines 101-164):**
- Preamble is pure local state: `BuildChatHistory` (new list), `OpenAIPromptExecutionSettings` (new instance), `kernel = _kernel.Clone()` if tools present (per-call clone, not shared).
- Provider call site: `_chatService.GetStreamingChatMessageContentsAsync(...)` inside `await foreach` (line 135-137).
- `FunctionCallContentBuilder` is a fresh local on line 134 — no cross-call state.
- Kernel clone is not retained beyond the method scope; the retry-replay path creates a fresh clone.

**MAF (`MafCompletionProvider.StreamAsync`, lines 131-209):**
- Preamble creates a **new `ChatClientAgent` per call** via `BuildAgent()` (line 137). Stateless.
- `functionCalls` dictionary and `anonymousCallIndex` are method-local (lines 164-165) — zero cross-call state.
- Provider call site: `agent.RunStreamingAsync(...)` inside `await foreach` (line 167-169).

**Neither adapter shares HTTP-client / connector state that could be observably mutated by a pre-first-delta failure.** The underlying `IChatClient` (MAF) / `IChatCompletionService` (SK) is the only shared object, and both SK's and MAF's streaming calls surface connection errors synchronously inside the initial SSE handshake, before any chunk has been dispatched to the iterator.

### Contract text (proposed)

> **`IStreamingCompletionProvider.StreamAsync` idempotence contract.** Implementations must guarantee that any exception thrown from `StreamAsync(request, ct)`, its preamble (synchronous setup before the first `await foreach` iteration), or the first `MoveNextAsync()` on the returned async enumerator leaves no observable side-effect on shared state — underlying HTTP connections, telemetry spans that the implementation did not also dispose with error status, session storage, or cached connector state. The agent core is entitled to retry by constructing a fresh enumerator from a new `StreamAsync` call on the same provider instance with the same `CompletionRequest`. Exceptions raised after the first `CompletionUpdate` is yielded are **not** retryable and surface to the caller as-is.

### Proposed tests (two per adapter)

**Test 1 — synchronous preamble failure retries safely.**

- Arrange: `IChatClient`/`IChatCompletionService` fake that throws `HttpRequestException` on the first `StreamAsync` call, succeeds (yields 3 deltas) on the second call.
- Assert: agent's observed stream yields 3 deltas; provider was invoked twice; no duplicate telemetry spans; no leaked enumerator.

**Test 2 — first-MoveNext failure retries safely.**

- Arrange: provider fake that yields `await foreach` OK but throws `IOException` on first `MoveNextAsync()` (simulating SSE stream terminating immediately). Second attempt yields 3 deltas.
- Assert: same as Test 1 — agent sees 3 deltas, provider invoked twice, no cross-attempt state leakage.

**Negative test (separate, exists for both adapters) — post-first-delta failure is not retried.**

- Arrange: provider fake yields delta #1 successfully, throws on delta #2's `MoveNextAsync`.
- Assert: agent yielded delta #1 to caller, agent surfaced the failure to the outer `failure` path, `TurnFailed` emitted, provider invoked once.

Both adapters pass these contracts incidentally today; the tests pin the behaviour so future adapter work can't regress the retry substrate.

### Decision (Q3): write-the-contract + 6 tests (2 positive + 1 negative per adapter)

No adapter re-implementation. Contract lives in `IStreamingCompletionProvider` XML docs; tests live in the adapter packages' test projects (SK parity + MAF parity).

---

## Q4 — Event / budget invariants under retry

### State machine (one call of `StreamAsync`)

```
[Call Entry]
  ↓ append user turn to session
  ↓ emit TurnStarted (ONCE per call, invariant under retry)
  ↓ start stopwatch, activity
  ↓
[Per-turn loop: turnIndex++]
  ↓ check MaxTurns / MaxDuration (per-turn)
  ↓ build request (context providers, packer, input guardrails)  ← NOT retried
  ↓
  ┌─── retry boundary (Q2) ───────────────────┐
  │ InvokeThroughStreamingFilters(req, ct)    │  ← Q1 chain
  │   → provider.StreamAsync(req, ct)         │
  │ open enumerator + await first MoveNext    │
  │ capture firstUpdate                       │
  │ ↳ Polly retries on transient, not OCE     │
  └───────────────────────────────────────────┘
  ↓ per delta (including firstUpdate):
  │   ├ filter.OnStreamDeltaAsync (each filter, in order)
  │   ├ accumulate text, ToolCalls, modelId, tokens
  │   └ yield delta to caller
  ↓
[Stream drained (per turn)]
  ↓ aggregate turn tokens → check MaxPromptTokens / MaxCompletionTokens
  ↓
  ├── no tool calls:
  │     filter.OnStreamCompleteAsync (each, in order)
  │     output guardrails
  │     loopDone = true
  └── tool calls:
        append assistant-with-tool-calls to workingHistory
        dispatch each (check MaxToolCalls)
        append tool-result turns to workingHistory
        loop back
  ↓
[Call Exit]
  ↓ append final assistant to session
  ↓ report usage (once, aggregated)
  ↓ emit TurnCompleted (or TurnFailed if failure set)
```

### Invariants

| # | invariant                                                       | where enforced                           |
|---|-----------------------------------------------------------------|------------------------------------------|
| 1 | `TurnStarted` fires exactly once per call, regardless of retries | emission happens before retry boundary   |
| 2 | Retry is invisible to the event bus                             | no event emitted inside Polly callback   |
| 3 | Token counts accumulate per turn, not per attempt               | first-delta may carry tokens; pre-first-delta failures carry none |
| 4 | Tool dispatch is outside the retry boundary                     | dispatches happen after the stream drains |
| 5 | `workingHistory` writes happen after the retry boundary         | writes are post-stream-drain today       |
| 6 | `OnStreamDeltaAsync` fires per delta yielded — never on replayed first-delta of a failed attempt | retry only replays pre-first-delta; first successful delta fires once |
| 7 | `OnStreamCompleteAsync` fires once per final-answer turn         | emission sits at end of non-tool-call path |
| 8 | Cancellation is not retried                                      | default pipeline's `ShouldHandle` excludes `OperationCanceledException` |

### Edge case — empty stream

Provider yields zero deltas (first `MoveNextAsync` returns `false` without throwing). Today's `StreamAsync` treats this as end-of-turn with empty text, runs output guardrails + `OnStreamCompleteAsync`, appends an empty assistant turn. Retry boundary does not change this: the loop exits the `ExecuteAsync` block with `streamLive = false`, then the existing "no tool calls ⇒ finalise" path fires with empty accumulator. Test this case explicitly (it's silent today).

### Edge case — cancellation during retry boundary

`ShouldHandle` excludes OCE ⇒ Polly rethrows immediately. Outer `catch (OperationCanceledException) { throw; }` preserves today's cancellation semantics. No `TurnFailed` emitted on cancellation (matches existing `AskAsync` behaviour).

### Edge case — filter throws before calling next

Streaming-filter `InvokeAsync` short-circuits by throwing (not yielding). Exception bubbles through `ExecuteAsync` — Polly's `ShouldHandle` may retry if the exception type qualifies. That's probably wrong: filter-thrown exceptions should not retry. **Sub-decision needed**: narrow the default `ShouldHandle` to the streaming pipeline to exclude filter-domain exceptions (a new marker, e.g. `AgentGuardrailDeniedException`, `AgentBudgetExceededException`, `AgentInterruptedException` all already exist and are non-retryable by intent). Pillar should add an `IsFilterDomainException` predicate alongside the retry.

---

## Verdict — ready to write the pillar plan

### Locked decisions

1. **Filter surface**: widen shipped `IStreamingAgentFilter` with an additive DIM `InvokeAsync(request, next, ct) : IAsyncEnumerable<CompletionUpdate>`. Single type, three override points, mirrors `IAgentFilter`'s role.
2. **Filter composition**: agent wraps the filter chain around the provider; agent drives `OnStreamDeltaAsync` + `OnStreamCompleteAsync` as today. Filters participating only in around-stream don't touch the per-delta hook.
3. **Retry strategy**: pre-first-delta-only. Polly wraps `enumerator.GetAsyncEnumerator` + first `MoveNextAsync`. Post-first-delta exceptions surface to the caller unretried.
4. **Retry boundary scope**: per-turn, inside the tool-call loop. Each streamed turn gets its own retry boundary; retries never replay previous turns.
5. **Adapter contract**: `IStreamingCompletionProvider` gets a normative contract clause — "no observable side-effect on shared state before first delta". Neither adapter needs code changes; both satisfy it by construction today. 6 tests (2 positive + 1 negative per adapter) pin the behaviour.
6. **`ShouldHandle` discipline**: retry pipeline used for streaming excludes `OperationCanceledException` (already default) AND agent-domain exceptions (`AgentGuardrailDeniedException`, `AgentBudgetExceededException`, `AgentInterruptedException`). One `IsFilterDomainException` helper in Core, reused by the default streaming-retry predicate.
7. **Event invariants**: `TurnStarted` / `TurnCompleted` fire once per call (unchanged); retry invisible to event bus; `OnStreamDeltaAsync` per yielded delta; `OnStreamCompleteAsync` once per final-answer turn.
8. **Budget invariants**: token accumulation per-turn, not per-attempt; tool dispatch outside retry boundary; `workingHistory` writes after retry.

### Proposed PR shape (3 PRs)

**PR 1 — Abstractions + Core.**
- Widen `IStreamingAgentFilter` with DIM `InvokeAsync(request, next, ct)` default-delegating to `next`.
- Add `IsFilterDomainException` helper in Core (internal static).
- Add `BuildDefaultStreamingPipeline()` in Core (internal; mirrors `BuildDefaultPipeline()` but narrower `ShouldHandle`).
- Refactor `StatefulAiAgent.StreamAsync` per-turn loop: introduce retry boundary, wire filter chain around provider, split enumerator open + drain into Phase 1/Phase 2.
- Add `StatefulAgentOptions.StreamingResiliencePipeline` (null ⇒ default).
- Contract text update on `IStreamingCompletionProvider` XML docs.
- Tests: 12+ — retry-success × (SK-fake + MAF-fake + in-process fake), post-first-delta-failure-no-retry, short-circuit cache filter, rate-limit filter, request-rewriter filter, cancellation-not-retried, empty-stream edge case, event-emission invariants.

**PR 2 — Adapter contract tests.**
- SK: 2 positive + 1 negative idempotence tests using deterministic fake `IChatCompletionService`.
- MAF: 2 positive + 1 negative idempotence tests using deterministic fake `IChatClient`.
- Parity test: same retry scenario green on both stacks.

**PR 3 — Cut `v0.10.0-preview`.**
- Unshipped → Shipped on Abstractions + Core + both adapters.
- Smoketest gains a streaming-retry probe + a short-circuit cached-response-filter probe.
- Pack + tag `v0.10.0-preview`; milestone log entry; research doc §7 backlog line struck through.

### Effort estimate

3 PRs, each ~1 focused session (retry state machine is the hardest, MAF/SK tests are rote parity). Adapter code needs **zero** changes (contract-only). Budget the pillar as 1.5–2 days of focused work — smaller than v0.9 (no new package, no new wire format, no Orleans surrogate work).

### Non-goals for v0.10

- **No new filter type.** Reuses the shipped `IStreamingAgentFilter` surface; DIM keeps it additive.
- **No buffer-everything fallback.** Consumers who want buffered filter behaviour stay on `AskAsync` as the research doc § 7 line documents.
- **No observability changes.** Per-attempt retry telemetry is adapter-/SK-/MAF-internal; our `chat` activity remains per-call. If an observability consumer wants per-attempt visibility, that's a v0.11+ conversation.
- **No journal integration.** v0.5's `IAgentJournal` is tool-call-granular; streamed-delta replay isn't part of the deterministic-replay goal (see § 7 "Temporal parity" roadmap).

---

## Open items (for pillar planning, not blockers)

- Naming of the DIM — `InvokeAsync` clashes with `OnStreamDeltaAsync` / `OnStreamCompleteAsync` visually on the same type. Alternative: `OnStreamingAsync(request, next, ct)` — reads "hook when streaming happens, you have a next". Decide at pillar-plan time.
- Whether `StreamingResiliencePipeline` should default to the same pipeline as `ResiliencePipeline` (identical retry strategy) or diverge (fewer retries, narrower `ShouldHandle`). Lean: same defaults, separate knob on options so consumers can differentiate without rebuilding one.
- Whether the spike skip of a code PoC was right. Reading the existing code gave unambiguous answers; a code spike would not have shifted Q1–Q4 conclusions. If the pillar surfaces an unexpected composition issue, we run a targeted spike then.
