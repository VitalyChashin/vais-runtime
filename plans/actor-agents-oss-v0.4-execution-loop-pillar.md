# v0.4.0-preview — Execution-loop pillar (§9.5 of the architectural review)

Tactical plan for the fifth pillar. Companion to [`actor-agents-oss-architecture-review.md`](./actor-agents-oss-architecture-review.md) §9.5. Created 2026-04-18.

---

## Scope

Introduce the primitives that give `StatefulAiAgent` top-level ownership of the turn loop: step/token/duration budget, streaming-filter hook, tool-call dispatcher, HITL interrupt, and the new `AgentEvent` subclasses (plus the deferred `GuardrailTriggered` event from §9.4) that ride along with those.

**Design decisions settled 2026-04-18**:

1. **Option (C) tool-call ownership** — `StatefulAiAgent` owns the outer loop (model call → detect tool calls → dispatch → repeat). SK/MAF adapters stop auto-invoking tools and instead surface tool-call requests as structured objects; `IToolCallDispatcher` runs each call. Canonical ReAct layering; gives us one well-defined boundary for budgets, interrupts, guardrails, and future journaling. ~50-line per-adapter change.
2. **PR split — five PRs** for this pillar (biggest pillar so far; split further after the Q1 design spike surfaced that adapter rewire + events + Orleans surrogate regen each deserve their own commit):
   - **PR 8**: `RunBudget` + `IStreamingAgentFilter` (additive, no event/tool churn). **Committed** as `404fcb6`.
   - **PR 9a**: Abstractions additions for tool calls (`ToolCallRequest`, `ToolCallOutcome`, `IToolCallDispatcher`, `AgentBudgetExceededException`; extend `CompletionResponse.ToolCalls`, extend `ChatTurn` with `ToolCalls` + `ToolCallId`, add `AgentChatRole.Tool`) + Core `DefaultToolCallDispatcher` + `StatefulAiAgent.AskAsync` outer loop + budget enforcement + `IToolGuardrail` wiring. Uses `FakeCompletionProvider` returning structured `ToolCalls`. **No adapter changes** — real SK/MAF never return tool calls in 9a (still auto-invoke), so loop runs once and exits like today. Zero regression.
   - **PR 9b**: SK + MAF rewire — `FunctionChoiceBehavior.None()` for SK, `UseProvidedChatClientAsIs = true` for MAF, map `FunctionCallContent` on output, translate tool-role `ChatTurn`s back to native message shapes on input. Parity tests. **Streaming-path tool accumulation deferred** to a follow-up — consumer impact zero (tool-using streaming simply unsupported in v0.4; AskAsync works fine).
   - **PR 9c**: `ToolCallStarted` + `ToolCallCompleted` + `GuardrailTriggered` event subclasses + Orleans `AgentEventSurrogate` regen (per-subclass converters per the M3e-3b finding) + `DefaultToolCallDispatcher` emits the tool events.
   - **PR 10**: `AgentInterrupt` + `ResumeInput` + `InterruptRaised` event (rides PR 9c's surrogate work); opt-in resume semantics.
3. **`CompletionResponse` shape choice — option (i)**. Extend with `IReadOnlyList<ToolCallRequest>? ToolCalls = null` as last positional param. Research confirmed the OpenAI wire allows `text + tool_calls` to coexist, so a discriminated union would corrupt data. Additive, `with`-expressions and existing ctor sites compile unchanged. UsageRecord / event-bus consumers untouched.
4. **`ChatTurn` extension**: add `IReadOnlyList<ToolCallRequest>? ToolCalls` (used when Role is Assistant with tool-call request) and `string? ToolCallId` (used when Role is Tool carrying a tool result). Minimal shape-extension; adapters in PR 9b translate these to native SK/MAF message types.
5. **Session vs. working history**: the session keeps only user + final-assistant turns. The tool-call intermediate messages (assistant-with-tool-calls, tool-result) live in an in-memory "working history" scoped to one `AskAsync` call. Keeps sessions clean for UI display; keeps provider requests faithful to the model's expected chat format.
6. **`StreamAsync` stays single-turn in 9a**. Tool-using streaming is deferred — consumers who want tools use `AskAsync`; streaming callers who don't use tools see no change. Multi-turn streaming is a future polish item, not v0.4.

---

## Delivery

### PR 8 — `RunBudget` + `IStreamingAgentFilter`

**Packages**: `Vais2.Agents.Abstractions`, `Vais2.Agents.Core`.

Tasks:

- [x] Abstractions: `RunBudget` record — `MaxTurns`, `MaxToolCalls`, `MaxPromptTokens`, `MaxCompletionTokens`, `MaxDuration` + static `Unlimited`.
- [x] Abstractions: `IStreamingAgentFilter` — default-interface-method hooks `OnStreamDeltaAsync` (pass-through default) + `OnStreamCompleteAsync` (no-op default).
- [x] Core: `StatefulAgentOptions.Budget` + `StatefulAgentOptions.StreamingFilters`.
- [x] Core: `StatefulAiAgent.StreamAsync` runs delta-filter chain inside the stream loop (transform or throw-to-abort); fires `OnStreamCompleteAsync` after drain, before output guardrails; exceptions from either method become `failure` and flow through the existing `TurnFailed` + usage-sink path.
- [x] Tests — 9 new: 2 `RunBudgetTests`, 7 `StreamingFilterTests` (no-filter pass-through, single transform, ordered multi-filter chain, complete-hook observes full response, delta-exception aborts + TurnFailed, complete-exception aborts after deltas, filter changes text but accumulator still uses post-filter text).
- [x] `PublicAPI.Unshipped.txt` updates.

Breaking-change ledger for PR 8:

- None. Pure additions. `Budget` stored but not enforced in this PR.

### PR 9a — Abstractions + Core loop (no adapter changes)

**Packages**: `Vais2.Agents.Abstractions`, `Vais2.Agents.Core`.

Tasks:

- [x] Abstractions: `ToolCallRequest(string ToolName, JsonElement Arguments, string CallId)` record.
- [x] Abstractions: `ToolCallOutcome(string CallId, string Result, string? Error = null)` record.
- [x] Abstractions: `IToolCallDispatcher.DispatchAsync(ToolCallRequest, AgentContext, CT) -> ValueTask<ToolCallOutcome>`.
- [x] Abstractions: `AgentBudgetExceededException(string BudgetField, object Limit, object Observed)` — carries which field + limit + observed value.
- [x] Abstractions: extend `CompletionResponse` with `IReadOnlyList<ToolCallRequest>? ToolCalls = null` (last positional param).
- [x] Abstractions: extend `ChatTurn` with `IReadOnlyList<ToolCallRequest>? ToolCalls` + `string? ToolCallId`.
- [x] Abstractions: `AgentChatRole.Tool = 3`.
- [x] Core: `DefaultToolCallDispatcher` resolves from registry, runs `Before`/`After` tool guardrails, wraps tool exceptions in `ToolCallOutcome.Error`. Guardrail denial throws `AgentGuardrailDeniedException(Layer.Tool, Reason)`. Silent (no events) in 9a per plan.
- [x] Core: `StatefulAgentOptions.ToolCallDispatcher` (nullable; auto-constructs a `DefaultToolCallDispatcher` from registry + guardrails when unset).
- [x] Core: `StatefulAiAgent.AskAsync` outer loop — round-by-round: build candidate from working history → run reducer + composer + context providers + packer + input guardrails → call provider via filter chain → if no tool calls, run output guardrails and break; else append assistant-with-ToolCalls + dispatch each call + append tool-role turn, loop. Session only gets user + final-assistant turns (working history carries the intermediate tool-call/tool-result turns for the provider). Budget enforced at every loop-entry (MaxTurns, MaxDuration), every provider-call completion (MaxPromptTokens, MaxCompletionTokens), and every tool dispatch (MaxToolCalls).
- [x] Core: `StreamAsync` unchanged — stays single-turn (no tool loop). Tool-using streaming is a deferred polish item per plan.
- [x] Tests — 12 new: 5 `DefaultToolCallDispatcherTests` (unknown tool → KeyNotFoundException in outcome, success, tool-exception wrapped, Before-deny throws without invoking, After-deny throws after invoking) + 7 `StatefulAiAgentToolLoopTests` (single-round-no-tools unchanged, tool-call-loops-then-returns-final-text with history propagation verified, MaxTurns / MaxToolCalls / MaxPromptTokens enforcement, aggregated-tokens in UsageRecord, tool guardrail deny fails with typed exception).
- [x] `PublicAPI.Unshipped.txt` updates in both packages; shipped `CompletionResponse` + `ChatTurn` ctor/Deconstruct signatures marked `*REMOVED*` in Unshipped with new signatures added alongside (record-additive-param pattern).

Breaking-change ledger for 9a:

- `CompletionResponse` record gains an additive last positional parameter. Existing `new CompletionResponse(text)` and `new CompletionResponse(text, model, prompt, compl)` keep compiling; `with`-expressions preserved.
- `ChatTurn` record gains two additive last positional parameters. Same semantics as above.
- `AgentChatRole` enum gains a new member at the end — source-compat (old switches on `Role` without a default may warn on exhaustiveness, but that's a code-smell anyway).
- `IAiAgent` gains no new surface.
- Real SK/MAF adapters see no behaviour change in 9a — they keep auto-invoking and never return tool calls, so the loop exits after round 1 identically to v0.3.

### PR 9b — SK + MAF adapter rewire

**Packages**: `Vais2.Agents.Ai.SemanticKernel`, `Vais2.Agents.Ai.MicrosoftAgentFramework`.

Tasks:

- [x] `SkCompletionProvider` non-streaming: `FunctionChoiceBehavior.Auto()` → `FunctionChoiceBehavior.None()`. After `GetChatMessageContentAsync`, pull `FunctionCallContent.GetFunctionCalls(result)` and map to `ToolCallRequest(FunctionName, SerializeToElement(KernelArguments ?? {}), Id ?? "")`. Set on `CompletionResponse.ToolCalls`.
- [x] `SkCompletionProvider` streaming path: kept on `.Auto()` by design — tool-using streaming via `StatefulAiAgent.StreamAsync` is unsupported in v0.4; direct `StreamAsync` callers still see SK's built-in auto-invocation, zero regression.
- [x] `MafCompletionProvider`: dropped `.UseFunctionInvocation()` at construction. Now constructs `ChatClientAgent` via the options-shaped ctor with `ChatClientAgentOptions { Name, Description, UseProvidedChatClientAsIs = true }`. **Found during implementation**: `ChatClientAgentOptions` has no `Instructions` property (only the positional ctor carries it) — so we front-load `request.SystemPrompt` as a `System`-role `MeaiChatMessage` prepended to the message list instead. Cleaner than the positional-ctor/options-ctor split anyway.
- [x] Tool-call extraction (MAF): iterate `response.Messages` for `MeaiFunctionCallContent` items, map to `ToolCallRequest(Name, SerializeToElement(Arguments ?? {}), CallId)`.
- [x] Input-side translation (both adapters): `ChatTurn(Assistant, text, ToolCalls=...)` → native message with `FunctionCallContent` items; `ChatTurn(Tool, result, ToolCallId=...)` → native tool-role message with `FunctionResultContent` item.
- [x] `SkToolBinder` / `MafToolBinder`: unchanged (tool advertising is orthogonal).
- [x] Parity tests: **zero changes required.** The MAF end-to-end parity test (`ScriptedChatClient` scripts a tool-call response then a final-text response) naturally works under the new flow: adapter surfaces the tool call, `StatefulAiAgent`'s outer loop (from PR 9a) dispatches, feeds result back, receives final text. Previously the test relied on `.UseFunctionInvocation()` middleware; now it relies on `StatefulAiAgent`'s loop. Same test, same assertions, same outcome.
- [x] `PublicAPI.Unshipped.txt` unchanged — pure behaviour change on adapter surface.
- [x] Small fix: `ChatMessageContent` type name ambiguity between `OpenAI.Chat` and `Microsoft.SemanticKernel` — aliased as `SkChatMessageContent` at the top of `SkCompletionProvider`.

Breaking-change ledger for 9b:

- SK/MAF adapter behaviour changes — consumers of `ICompletionProvider` directly (not through `StatefulAiAgent`) who previously relied on auto-invoke now see `CompletionResponse.ToolCalls` populated and must handle them. Documented in adapter XML.
- MAF streaming path no longer auto-invokes (the `.UseFunctionInvocation()` wrapping covered both paths). Consumers calling `MafCompletionProvider.StreamAsync` directly with tools will see empty deltas when the model requests tools. Documented. SK streaming still auto-invokes — the behaviour asymmetry matches the v0.4 policy "tool-using streaming via `StatefulAiAgent` is undefined".

### PR 9c — Events + Orleans surrogate regen

**Packages**: `Vais2.Agents.Abstractions`, `Vais2.Agents.Core`, `Vais2.Agents.Hosting.Orleans`.

Tasks:

- [x] Abstractions: new `AgentEvent` subclasses — `ToolCallStarted(CallId, ToolName)`, `ToolCallCompleted(CallId, ToolName, Succeeded, Error, Duration)`, `GuardrailTriggered(Layer, Decision, Reason)`.
- [x] Core: `DefaultToolCallDispatcher` takes optional `IAgentEventBus eventBus` ctor arg; emits `ToolCallStarted` before invocation, `ToolCallCompleted` after (both success and tool-exception paths), and `GuardrailTriggered` on tool-layer denial. `StatefulAiAgent` emits `GuardrailTriggered` on input/output-layer denial before the throw. `StatefulAiAgent` passes its `_eventBus` to the default dispatcher when constructing one.
- [x] `Hosting.Orleans`: `AgentEventSurrogate` gains 6 new fields (CallId, ToolName, Succeeded, GuardrailLayer, GuardrailDecision, GuardrailReason — Ids 11-16). `AgentEventKind` gains 3 new values (ToolCallStarted=3, ToolCallCompleted=4, GuardrailTriggered=5). Three new per-subclass `IConverter<Subclass, AgentEventSurrogate>` registered (per the M3e-3b finding — surrogate dispatch is exact-runtime-type).
- [x] Tests — 5 new emission tests in Core (`ToolCallEventEmissionTests`: success pair, tool-exception pair with Error, Before-deny emits only `GuardrailTriggered`, After-deny emits all three, Output-layer `GuardrailTriggered` via `StatefulAiAgent`). 3 new round-trip tests in Hosting.Orleans (`ToolCallStarted`, `ToolCallCompleted`, `GuardrailTriggered`). Existing `Denial_Emits_TurnFailed_On_Event_Bus` Core test updated to expect the new `GuardrailTriggered` event in the sequence.
- [x] `PublicAPI.Unshipped.txt` updates: 35 new entries in Abstractions (record auto-members for 3 subclasses), 1 line change in Core (`DefaultToolCallDispatcher` ctor param added), 12 new entries in Hosting.Orleans (3 converters + 3 enum values + 6 surrogate fields).

Breaking-change ledger for 9c:

- `AgentEvent` closed hierarchy gains three subclasses. Additive; consumers pattern-matching exhaustively need to handle the new cases (same concern we flagged in the arch review's breaking-change accounting).
- `DefaultToolCallDispatcher` ctor gains an optional `IAgentEventBus? eventBus = null` last parameter. Source-compatible. Shipped surface already listed the existing two-param ctor as unshipped so the change is invisible outside the unshipped file.

### PR 10 — `AgentInterrupt` + resume semantics

**Packages**: `Vais2.Agents.Abstractions`, `Vais2.Agents.Core`, `Vais2.Agents.Hosting.Orleans`.

Tasks:

- [x] Abstractions: `AgentInterrupt(InterruptId, Reason, Payload)` record, `ResumeInput(InterruptId, Payload)` record, `InterruptRaised(At, Context, InterruptId, Reason)` event subclass, `AgentInterruptedException` carrying `AgentInterrupt`.
- [x] Abstractions: `GuardrailDecision.Interrupt = 2` enum value (the deferred value from PR 7), plus `GuardrailOutcome.InterruptPayload` field (renamed from `Interrupt` to avoid collision with the static factory of the same name) and `GuardrailOutcome.Interrupt(AgentInterrupt, string?)` factory.
- [x] Core: `StatefulAiAgent` guardrail runners + `DefaultToolCallDispatcher` tool-guardrail branches both handle the `Interrupt` decision — emit `InterruptRaised` on the bus, throw `AgentInterruptedException`. Missing-payload misuse throws a clear `InvalidOperationException` instead of a null-ref.
- [x] Core: `StatefulAiAgent.ResumeAsync(ResumeInput, CT)` — v0.4 shim treating `ResumeInput.Payload` as the next user turn (JSON string → user message, object/array → `.ToString()`'d); true mid-loop resume deferred to the durable-execution pillar as option (b) per the conversation.
- [x] Hosting.Orleans: `AgentEventSurrogate` gains `InterruptId` + `InterruptReason` fields (Ids 17-18). `AgentEventKind.InterruptRaised = 6`. `InterruptRaisedSurrogateConverter` registered per M3e-3b pattern.
- [x] Tests — 10 new in Core (2 exception-shape, 3 factory, 5 integration: input-guardrail interrupt throws with payload, output-guardrail interrupt emits InterruptRaised + TurnFailed, ResumeAsync null-rejection, ResumeAsync forwards payload as user turn, missing-payload → descriptive InvalidOperationException) + 1 Orleans round-trip for `InterruptRaised`. **225/225 non-container green**, +11 from 214.
- [x] `PublicAPI.Unshipped.txt` updates.

Breaking-change ledger for PR 10:

- `GuardrailDecision` enum gains `Interrupt = 2` (additive at end — source-compat).
- `GuardrailOutcome` record gains an additive last positional parameter `InterruptPayload`. Existing `new GuardrailOutcome(Decision)` and `new GuardrailOutcome(Decision, Reason)` callsites compile unchanged. No REMOVED markers needed since `GuardrailOutcome` was unshipped in PR 7.
- `AgentEvent` closed hierarchy gains `InterruptRaised` — 7 subclasses total.

---

## Exit criteria for the pillar

- All three PRs merged to OSS repo `main`.
- `IToolGuardrail` wired (closes §9.4 deferred item).
- `GuardrailTriggered` event shipped (closes §9.4 deferred item).
- Orleans `AgentEvent` surrogate covers all seven subclasses.
- Full test suite green.
- Milestone entries in `actor-agents-oss-extraction-research.md` §8.

---

## Progress log

- 2026-04-18 — plan created, design decisions settled (option C tool-call ownership, three-PR split).
- 2026-04-18 — PR 8 complete on local working tree. `RunBudget` + `IStreamingAgentFilter` types ship; streaming-filter chain wired in `StreamAsync`. Budget is carried on options but not enforced yet — waits on PR 9. 9 new tests, 194/194 non-container green, 0 warnings.
- 2026-04-18 — PR 8 committed as `404fcb6` on OSS repo `main` (not pushed).
- 2026-04-18 — Design spike completed (MAF/SK no-auto-invoke paths verified, `CompletionResponse` shape = option (i), 3-PR split approved).
- 2026-04-18 — PR 9a complete on local working tree. `ToolCallRequest` / `ToolCallOutcome` / `IToolCallDispatcher` / `AgentBudgetExceededException` in Abstractions; additive params on `CompletionResponse` and `ChatTurn`; `AgentChatRole.Tool = 3`. Core: `DefaultToolCallDispatcher` with tool-guardrail wiring, `StatefulAgentOptions.ToolCallDispatcher`, `StatefulAiAgent.AskAsync` rewritten as outer loop with full budget enforcement. Working-history / session-history split keeps sessions clean. 12 new tests, 206/206 non-container green, 0 warnings.
- 2026-04-18 — PR 9a committed as `a69a66e` on OSS repo `main` (not pushed).
- 2026-04-18 — PR 9b complete on local working tree. SK adapter: `.Auto()` → `.None()` on non-streaming, extract `FunctionCallContent` into `CompletionResponse.ToolCalls`, translate `ChatTurn.Tool` + `ChatTurn.Assistant(ToolCalls=...)` back to SK `ChatMessageContent` shapes on input. MAF adapter: dropped `.UseFunctionInvocation()`, construct `ChatClientAgent` with `ChatClientAgentOptions { UseProvidedChatClientAsIs = true }`, moved system-prompt to a prepended `System`-role message (options ctor doesn't carry `Instructions`), extract tool calls from `response.Messages` `FunctionCallContent` items, translate `ChatTurn` tool/assistant shapes to MEAI. **Parity tests: zero changes needed** — the MAF end-to-end test now exercises the new loop instead of `.UseFunctionInvocation()`, same assertions pass. 0 new tests; still 206/206 non-container green, 0 warnings.
- 2026-04-18 — PR 9b committed as `01becee` on OSS repo `main` (not pushed).
- 2026-04-18 — PR 9c complete on local working tree. Abstractions: 3 new `AgentEvent` subclasses (`ToolCallStarted`, `ToolCallCompleted`, `GuardrailTriggered`) — closed hierarchy now has 6 subclasses total. Core: `DefaultToolCallDispatcher` wires optional `IAgentEventBus` + emits tool-call events + tool-layer `GuardrailTriggered`; `StatefulAiAgent` emits input/output-layer `GuardrailTriggered` before the throw. Orleans: `AgentEventSurrogate` extended with 6 new fields (Ids 11-16), `AgentEventKind` with 3 new values, 3 new per-subclass converters added (per M3e-3b pattern). 8 new tests (5 emission in Core + 3 Orleans round-trip for new subclasses), 1 existing test updated for the new `GuardrailTriggered` emission. 214/214 non-container green, 0 warnings.
- 2026-04-18 — PR 9c committed as `0b58365` on OSS repo `main` (not pushed).
- 2026-04-18 — PR 10 complete on local working tree. Abstractions: `AgentInterrupt`, `ResumeInput`, `AgentInterruptedException`, `InterruptRaised` event (7th subclass), `GuardrailDecision.Interrupt = 2` (closes PR 7 deferral), `GuardrailOutcome.InterruptPayload` + `.Interrupt(...)` factory. Core: `StatefulAiAgent` + `DefaultToolCallDispatcher` handle Interrupt decisions → emit `InterruptRaised` → throw `AgentInterruptedException`. `StatefulAiAgent.ResumeAsync(ResumeInput, CT)` v0.4 shim that forwards `ResumeInput.Payload` as the next user turn; durable mid-loop resume deferred to the post-v0.4 durable-execution pillar. Orleans: `AgentEventSurrogate` + `AgentEventKind.InterruptRaised = 6` + `InterruptRaisedSurrogateConverter`. 11 new tests, 225/225 non-container green, 0 warnings.
- 2026-04-19 — **follow-up: tool-using streaming closed.** Local working tree on OSS repo `main`. Same additive shape as the rest of the pillar — no agent-surface break on `StatefulAiAgent.StreamAsync` (still `IAsyncEnumerable<string>`). Changes:
  - **Abstractions** (unshipped): `CompletionUpdate` gained a nullable trailing `ToolCalls : IReadOnlyList<ToolCallRequest>?` field. Providers emit a terminal `CompletionUpdate` whose `ToolCalls` is populated when the model ends a streamed turn with tool requests; `TextDelta` on that terminal update is typically empty. Last-non-null-wins aggregation rule matches how `ModelId` / token counts already work. `PublicAPI.Shipped` ctor + `Deconstruct` entries swapped via `*REMOVED*` markers in `Unshipped` — not a public break since `0.4` hasn't been pushed.
  - **Core**: `StatefulAiAgent.StreamAsync` rewritten as an outer tool-call loop parallel to `AskAsync`. Working-history / session-history split preserved; session still only sees user + final assistant. Budget enforcement turn-by-turn (`MaxTurns`, `MaxDuration`, `MaxPromptTokens`, `MaxCompletionTokens`, `MaxToolCalls`). Input guardrails fire at every streamed turn; output guardrails + `OnStreamCompleteAsync` fire once at the final (non-tool-call) turn, same post-facto semantics as v0.4 streaming. A single `TurnStarted` / `TurnCompleted` envelopes the whole run; `ToolCallStarted` / `ToolCallCompleted` flow through the dispatcher as usual between streamed turns.
  - **SK adapter**: flipped `SkCompletionProvider.StreamAsync` from `FunctionChoiceBehavior.Auto()` to `.None()` — matches the non-streaming path. Tool-call fragments across chunks are accumulated via SK's built-in `FunctionCallContentBuilder`; at stream drain the adapter emits one terminal `CompletionUpdate` whose `ToolCalls` is the rebuilt list mapped to `ToolCallRequest`.
  - **MAF adapter**: `MafCompletionProvider.StreamAsync` now walks every `AgentRunResponseUpdate.Contents` looking for `FunctionCallContent` items, accumulates into a dict keyed by `CallId` (last-seen-wins per id; anonymous call ids get synthetic keys). At stream drain, emits one terminal `CompletionUpdate` with the full list. MEAI's streaming already surfaces whole `FunctionCallContent` (not arg-string fragments) so per-id last-write-wins is correct for MEAI-compliant clients; documented.
  - **Tests**: 5 new `StatefulAiAgentStreamingToolCallTests` in Core (round-trip dispatch + continuation, working-history propagation across turns, `MaxToolCalls` budget, `MaxTurns` budget, event-bus emission) + new `ScriptedMultiTurnStreamingProvider` test double. 2 new `StreamingToolCallingParityTests` (MAF end-to-end round-trip + SK adapter terminal-tool-call emission). `ScriptedStreamingChatClient` + `ScriptedStreamingChatCompletionService` extended with per-call script + function-call-update ctor overloads (existing single-turn string ctors retained for the original `StreamingParityTests`). **280/280 non-container tests green** (+7 vs. the previous 273 baseline). Smoketest repacked against the four changed packages (Abstractions + Core + both adapters) and re-ran cleanly, with `CompletionUpdate(ToolCalls: ...)` probed explicitly.
  - **Still deferred**: `IAgentFilter` (synchronous filters) and `ResiliencePipeline` remain bypassed on the streaming path — same rationale as v0.4 (wrapping a stream either buffers the response or needs a new surface). Consumers needing filters stay on `AskAsync`.
- _(PR 10 entry to follow)_
- _(pillar closure entry to follow)_
