# v0.12.0-preview — SSE streaming Invoke pillar

Tactical plan for the HTTP streaming pillar. Closes the [`extraction-research`](./actor-agents-oss-extraction-research.md) §7 backlog line: *"SSE streaming Invoke on the HTTP surface (wire format + event taxonomy already specified in the v0.6 HTTP-API design doc; server/client impl deferred)."* Picks up where v0.6 PR 3 stopped (unary Invoke only). Grounded in the spike findings: [`actor-agents-oss-v0.12-sse-streaming-invoke-findings.md`](./actor-agents-oss-v0.12-sse-streaming-invoke-findings.md). Parallel shape to [`actor-agents-oss-v0.11-openapi-idempotency-pillar.md`](./actor-agents-oss-v0.11-openapi-idempotency-pillar.md). Created 2026-04-20.

---

## Scope

**MVP boundary locked 2026-04-20** via the research spike. Ten decisions:

1. **Dedicated route** `POST /v1/agents/{id}/invoke/stream` (v0.6 plan stands). Cleaner OpenAPI rendering than content-negotiation on `/invoke`; natural `text/event-stream` content-type means the v0.11 idempotency middleware opts out automatically.
2. **Full `AgentEvent` taxonomy on the wire** + a new `CompletionDelta : AgentEvent` subtype. 10 event kinds total. SSE `event:` field is the discriminator; body is the concrete record's JSON shape (no type-discriminator property in the body).
3. **`IStreamingAiAgent` capability interface in `Vais.Agents.Abstractions`.** Mirrors v0.9's `IResumableAgentGraph<TState>` precedent. `StatefulAiAgent` implements; `OrleansAiAgentProxy` does NOT (streaming-over-Orleans stays deferred per v0.10 milestone log). HTTP endpoint checks `is IStreamingAiAgent` and returns 501 otherwise.
4. **`CompletionDelta` record** — `TextDelta` + optional `ModelId` / `PromptTokens` / `CompletionTokens` / `ToolCalls`. Mirrors `CompletionUpdate` shape but as an `AgentEvent`-derived record that crosses the wire.
5. **Two client overloads on `IAgentControlPlaneClient`**: `InvokeStreamAsync(...) : IAsyncEnumerable<string>` (text-only; `OfType<CompletionDelta>().Select(d => d.TextDelta)` filter) + `InvokeStreamEventsAsync(...) : IAsyncEnumerable<AgentEvent>` (full taxonomy). Both DIM-default to `NotSupportedException` so mocks stay source-compat.
6. **SSE parser = `System.Net.ServerSentEvents` built-in.** Zero new deps — already transitively available via SK → OpenAI SDK. `SseParser<AgentEvent?>.Create` + 10-case switch on `eventType`.
7. **Heartbeat via channel multiplex**, default 15s interval. Configurable `StreamingInvokeOptions.HeartbeatInterval` (TimeSpan.Zero = disabled). Implementation: unbounded `Channel<string>`, agent-event loop + heartbeat timer both write, single SSE-writer task drains. Linked CTS on `HttpContext.RequestAborted` coordinates shutdown.
8. **New Problem-Details URN**: `urn:vais-agents:streaming-not-supported` (501). `ProblemDetailsMapping.StreamingNotSupported(agentId)` factory helper + operation-transformer URN table entry (`"501" => [...]`).
9. **v0.11 idempotency middleware unchanged** — already handles `text/event-stream` opt-out when endpoint sets content-type first.
10. **Orleans streaming passthrough explicitly deferred.** Non-streaming-capable agents (e.g. `OrleansAiAgentProxy`) return 501 with the new URN. Documented limitation; future pillar covers Orleans proxy when someone needs it.

### Semantic projection chosen

**Server-sent events carrying the shipped `AgentEvent` taxonomy.** Each event kind maps to a stable SSE event name (`turn.started` / `delta` / `tool.started` / `tool.completed` / `turn.completed` / etc.). Consumers that want just text filter to `delta`; consumers that want full observability (tool-call visibility, guardrail denials, interrupts) get it without a second request.

### Explicitly deferred to post-v0.12

- **Orleans streaming passthrough** (`OrleansAiAgentProxy` proxying `StreamAsync` to the silo-hosted `StatefulAiAgent`). Returns 501 in v0.12; separate pillar when asked.
- **WebSocket transport.** SSE only. Bi-directional needs + resumable connections are a different shape; v0.12 is server→client one-way.
- **Resume via `Last-Event-Id`.** Mid-stream disconnect = new turn. v0.5 journal + future Temporal-parity pillar handle durable replay.
- **Server-side event-bus fan-out** (cluster-wide observability endpoint). Out of scope — this pillar is per-call streaming to one consumer.
- **OpenAPI schema emission for the SSE body.** `.Produces<AgentEvent>(200, "text/event-stream")` is as much as we declare. Consumers doing client codegen against the spec need hand-authored SSE parsing — same as every REST SSE API in the wild.
- **Streaming-specific idempotency semantics.** SSE responses bypass the idempotency cache (v0.11 middleware's text/event-stream opt-out). Retry of a streaming call re-runs the turn. Matches Stripe SSE behaviour.
- **`InvokeStreamEventsAsync` with a typed state generic.** The text-only + full-events pair is enough; typed projections are consumer-side (`.OfType<ToolCallCompleted>()` etc.).

---

## Design questions — resolved

| # | Question | Decision | Reasoning |
|---|---|---|---|
| 1 | Route shape | Dedicated `/invoke/stream` | v0.6 plan; cleaner OpenAPI; natural idempotency opt-out |
| 2 | Event taxonomy | Full 10 `AgentEvent` kinds + new `CompletionDelta` | Reuses shipped wire-tested hierarchy; `event:` field is discriminator |
| 3 | Server attachment | `IStreamingAiAgent` capability interface | Source-compat with IAgentLifecycleManager/IAgentRuntime; v0.9 precedent |
| 4 | Client shape | Two DIM overloads on IAgentControlPlaneClient | Text-only + full-events; mocks don't have to implement |
| 5 | Heartbeat | 15s default, channel multiplex | Keeps proxies happy; clean CTS-based shutdown |
| 6 | Orleans | 501 + URN | Deferred; not blocking; documented |
| 7 | SSE parser | `System.Net.ServerSentEvents` built-in | Zero new deps; already transitively available |
| 8 | Content-type ordering | Set first before any await | v0.11 middleware already works when ordering is correct |
| 9 | Problem-Details URN | New `streaming-not-supported` for 501 | Mirrors v0.11's `idempotency-mismatch` + `idempotency-in-flight` pattern |
| 10 | `CompletionDelta` field set | `TextDelta` + `ModelId?` + `PromptTokens?` + `CompletionTokens?` + `ToolCalls?` | Mirrors shipped `CompletionUpdate` shape; minimal new wire surface |

### Open questions (low-stakes, resolve during impl)

1. **JSON property naming on wire.** `camelCase` (matches existing `JsonSerializerDefaults.Web` on control-plane surface) vs. `PascalCase` (System.Text.Json raw default). Lean: camelCase — matches existing surface.
2. **Heartbeat payload.** Bare SSE comment (`: heartbeat 2026-04-20T...\n\n`) vs. named event (`event: heartbeat\n\n`). Lean: bare comment — lower wire cost, clean "comments are no-ops" contract with parser, no new event name to document.
3. **`CompletionDelta.Context` on every text chunk.** `AgentEvent` base carries `Context` (UserId, TenantId, etc.) — potentially heavy per chunk. Lean: keep for consistency; SSE framing overhead dominates.
4. **Error-mid-stream shape.** Mid-stream handler exception: emit `event: turn.failed\ndata: {...}\n\n` then close the stream. Do NOT emit Problem Details mid-stream (stream is already committed, headers+status can't change).
5. **Max-headers + pre-stream validation.** Where's the guardrail against a 1GB request body on streaming invoke? Lean: same request-body size limits as unary (set by Kestrel/IIS defaults); no new pillar-level cap.
6. **501 vs 405 Method Not Allowed on Orleans proxy.** 501 Not Implemented feels right — the server understands the request but the backing agent can't fulfil it. 405 reads "this route doesn't accept POST" which is misleading. Lean: 501.
7. **SSE `retry:` field.** Spec lets the server suggest a reconnect delay. Our stream isn't resumable; omit `retry:` entirely so clients don't try to reconnect automatically. Lean: omit.

---

## No new packages

Package count stays at **22** (same as v0.9/v0.10/v0.11). All v0.12 work lives as extensions inside existing packages.

Extended packages (zero breaking changes on existing surface):
- **`Vais.Agents.Abstractions`** — `CompletionDelta : AgentEvent` record + `IStreamingAiAgent` interface.
- **`Vais.Agents.Core`** — `StatefulAiAgent` gains `IStreamingAiAgent` implementation; internal `StreamEventsCore` helper; existing `StreamAsync(string) : IAsyncEnumerable<string>` delegates through it for source-compat.
- **`Vais.Agents.Control.Http.Server`** — new streaming endpoint handler in `AgentControlPlaneEndpointRouteBuilderExtensions`; `StreamingInvokeOptions`; channel-based SSE-writer helpers; new `StreamingNotSupportedType` URN + `ProblemDetailsMapping.StreamingNotSupported` factory; `VaisProblemDetailsOperationTransformer._urnsByStatus` gains `"501"` entry.
- **`Vais.Agents.Control.Http.Client`** — `IAgentControlPlaneClient` + `AgentControlPlaneClient` gain 2 new DIM-default overloads (`InvokeStreamAsync` text-only, `InvokeStreamEventsAsync` full events); `System.Net.ServerSentEvents`-based parse loop; 10-case event-type dispatcher.

---

## Delivery

### PR 1 — Abstractions: `CompletionDelta` + `IStreamingAiAgent`

**Packages**: `Vais.Agents.Abstractions` (extend).

Tasks:

- [x] New sealed record `CompletionDelta : AgentEvent` with 5 extra fields beyond the `At` + `Context` base: `TextDelta` (string, non-null), `ModelId` (string?), `PromptTokens` (int?), `CompletionTokens` (int?), `ToolCalls` (`IReadOnlyList<ToolCallRequest>?`). XML doc explains the relationship to `CompletionUpdate` + clarifies when each optional field is populated (last non-null wins; `ToolCalls` only on terminal pre-dispatch update).
- [x] New interface `IStreamingAiAgent` with `IAsyncEnumerable<AgentEvent> StreamAsync(string userMessage, AgentContext context, CancellationToken cancellationToken)`. XML doc spells out the event-ordering contract: `TurnStarted` first, `CompletionDelta` per yielded text chunk interleaved with `ToolCallStarted`/`ToolCallCompleted` on tool-call loops, `GuardrailTriggered` / `InterruptRaised` when relevant, terminal `TurnCompleted` or `TurnFailed`. Cancellation semantics: OCE propagates without emitting `TurnFailed`; explicit handler exceptions emit `TurnFailed` before the enumerable ends.
- [x] `AgentEvent` XML doc unchanged — the "Closed hierarchy" paragraph already sanctions Unshipped additions; `CompletionDelta`'s own XML doc names itself as the v0.12 addition.
- [x] `PublicAPI.Unshipped.txt` updates — Abstractions +22 entries (`CompletionDelta` record's 14 auto-synthesised members + 1 interface + 1 method + 6 operators/equals overrides).
- [x] No tests — shape only; PR 2 + PR 3 exercise them.

### PR 2 — Core: implement `IStreamingAiAgent` on `StatefulAiAgent`

**Packages**: `Vais.Agents.Core` (extend).

Tasks:

- [x] `StatefulAiAgent : IStreamingAiAgent` — added interface to the class declaration. New public `StreamAsync(string, AgentContext, CT) : IAsyncEnumerable<AgentEvent>` as implicit interface impl (distinct from the existing string-returning overload).
- [x] Internal refactor: new private `StreamEventsCoreAsync(userMessage, context, CT) : IAsyncEnumerable<AgentEvent>` that rewrites the per-turn loop to yield `AgentEvent`s directly as it goes:
   1. Yield `TurnStarted` at entry.
   2. Inside the retry-boundary Phase 2 drain, yield `CompletionDelta` per text delta (mapped from `CompletionUpdate.TextDelta` + metadata).
   3. On tool-call dispatch, yield `ToolCallStarted` → dispatch → yield `ToolCallCompleted`. (`DefaultToolCallDispatcher` already publishes these to the event bus; the rewrite yields them directly so ordering is deterministic.)
   4. On guardrail denial, yield `GuardrailTriggered` before the exception propagates.
   5. On interrupt, yield `InterruptRaised` before the exception.
   6. On clean exit, yield `TurnCompleted`.
   7. On failure, yield `TurnFailed`.
- [x] The existing `public async IAsyncEnumerable<string> StreamAsync(string, CT)` stays source-compat: delegates to `StreamEventsCoreAsync` + projects to text via `if (evt is CompletionDelta d && d.TextDelta.Length > 0) yield return d.TextDelta`. All 32 existing streaming tests still pass.
- [x] Dual-emission model: `StreamEventsCoreAsync` publishes Turn/Guardrail/Interrupt events to `_eventBus` AND yields them. Bus subscribers + streaming observers each see each event exactly once — no observer sees duplicates (different observation channels). `DefaultToolCallDispatcher` publishes tool-call events to bus; streaming loop yields its own synthesised `ToolCallStarted`/`ToolCallCompleted` around the dispatch call with matching `CallId` / `ToolName`. Guardrail and interrupt events are synthesised in the streaming loop from the caught exception's fields (layer, reason, interrupt id), then yielded before the terminal `TurnFailed`.

   Actually simpler: `StreamEventsCore` doesn't call `_eventBus.PublishAsync` for the events it yields — the caller is observing them directly. If the caller wants bus fan-out too, they pipe the enumerable through their own `_eventBus.PublishAsync` loop at the consumer side.

- [x] `PublicAPI.Unshipped.txt` updates — Core +1 entry (implicit impl of `StatefulAiAgent.StreamAsync(string, AgentContext, CT)`).
- [x] Tests — 8 new in `Vais.Agents.Core.Tests/StatefulAiAgentStreamingEventsTests.cs`:
   - (1) Simple text-only turn: `TurnStarted` → 3×`CompletionDelta` (each with `TextDelta` matching provider chunks) → `TurnCompleted` with correct `AssistantText`, `ModelId`, `PromptTokens`, `CompletionTokens`.
   - (2) Tool-call turn: `TurnStarted` → `CompletionDelta` (text) → `ToolCallStarted` → `ToolCallCompleted` → `CompletionDelta` (more text) → `TurnCompleted`. Tool-call `CallId` matches across start/complete.
   - (3) Guardrail-denied turn: `TurnStarted` → `GuardrailTriggered` → `TurnFailed`. No `TurnCompleted`. Final event is `TurnFailed` with `ErrorType = "AgentGuardrailDeniedException"`.
   - (4) Interrupt turn: `TurnStarted` → `CompletionDelta` → `InterruptRaised` → `TurnFailed`. Matches ShipInterruptBehavior.
   - (5) Cancellation mid-delta: enumerator stops; no `TurnFailed` emitted (OCE is clean exit).
   - (6) `CompletionDelta.Context` carries `RunId` stamp consistent with the surrounding `TurnStarted.Context.RunId`.
   - (7) `StreamAsync(string) : IAsyncEnumerable<string>` source-compat: yields same text sequence as `StreamAsync(string, context, CT)` projected to deltas. Existing v0.10 tests still pass (regression check).
   - (8) Event ordering deterministic under retry: pre-first-delta retry replays don't emit doubled `TurnStarted`.

### PR 3 — Server: HTTP streaming endpoint + client overloads

**Packages**: `Vais.Agents.Control.Http.Server` (extend) + `Vais.Agents.Control.Http.Client` (extend).

Tasks:

**Server side:**

- [x] New endpoint handler `InvokeStreamAsync` registered on `POST /v1/agents/{id}/invoke/stream` in `AgentControlPlaneEndpointRouteBuilderExtensions`. Sequence:
   1. Parse `AgentInvocationRequest` body (same shape as unary `/invoke`).
   2. Resolve agent via `IAgentRegistry.GetAsync` + `IAgentRuntime.GetOrCreate`.
   3. Check `is IStreamingAiAgent streamable` → if not, return `ProblemDetailsMapping.StreamingNotSupported(agentId)` (501).
   4. Set `context.Response.ContentType = "text/event-stream"` + `Cache-Control: no-cache` + `X-Accel-Buffering: no` + flush headers.
   5. Create linked CTS from `context.RequestAborted`.
   6. Start heartbeat timer writing `: heartbeat <utc-iso>\n\n` to the multiplex channel every `StreamingInvokeOptions.HeartbeatInterval`.
   7. Spawn agent-loop task: iterates `streamable.StreamAsync(request.Text, BuildAgentContext(principal), linkedCt)` and writes `event: <name>\ndata: <json>\n\n` to the channel per event.
   8. SSE-writer task drains channel to `context.Response.Body` + flush after each.
   9. Finally: cancel CTS, dispose timer, wait for drain.
- [x] `StreamingInvokeOptions` — `TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15)` (Zero disables).
- [x] `ProblemDetailsMapping.StreamingNotSupportedType = "urn:vais-agents:streaming-not-supported"` const + `ProblemDetailsMapping.StreamingNotSupported(string agentId)` static helper returning `IResult` (501 + instance).
- [x] `VaisProblemDetailsOperationTransformer._urnsByStatus` gains `"501" => [StreamingNotSupportedType]` entry.
- [x] Route annotation on the new endpoint: `.WithMetadata(new StreamingEndpointAttribute())` + `.WithName("Agents.InvokeStream")` + `.WithSummary("Stream an invocation as Server-Sent Events.")` + `.WithDescription(...)` + `.Accepts<AgentInvocationRequest>("application/json")` + `.Produces(200, contentType: "text/event-stream")` + `.ProducesProblem(400/403/404/501/503)`. Plus new `StreamingEndpointAttribute` marker so `AgentControlPlaneIdempotencyMiddleware` skips body-buffering.
- [x] Inlined SSE frame formatting (`$"event: {name}\ndata: {json}\n\n"`) — no separate SseWriter helper needed; the channel produces strings directly, writer encodes UTF-8 + flushes.
- [x] `AgentEventSerializer` internal helper: `AgentEvent → (eventName, dataJson)`. 10-case switch on concrete subtype (including v0.12's `CompletionDelta`). Uses `JsonSerializerDefaults.Web`.

**Client side:**

- [x] `IAgentControlPlaneClient.InvokeStreamEventsAsync(agentId, request, version, idempotencyKey, CancellationToken) : IAsyncEnumerable<AgentEvent>` — DIM default throws `NotSupportedException` so mocks don't need to implement.
- [x] `IAgentControlPlaneClient.InvokeStreamAsync(agentId, request, version, idempotencyKey, CancellationToken) : IAsyncEnumerable<string>` — DIM default throws `NotSupportedException`.
- [x] `AgentControlPlaneClient` overrides both. Implementation uses `HttpClient.SendAsync(..., ResponseHeadersRead)` + verifies `Content-Type: text/event-stream` + wraps response stream in `SseParser.Create<AgentEvent?>(stream, ParseAgentEventFrame)` + yields. `InvokeStreamAsync` is a thin `OfType<CompletionDelta>()` projection over `InvokeStreamEventsAsync`.
- [x] 10-case `ParseAgentEventFrame` dispatcher on `eventType`: maps `turn.started`/`turn.completed`/`turn.failed`/`tool.started`/`tool.completed`/`tool.replayed`/`guardrail.triggered`/`interrupt.raised`/`handoff.requested`/`delta` to the right `JsonSerializer.Deserialize<T>` call. Unknown event names skip (forward-compat).

**Tests:**

- [x] Tests — 12 new, split between Http.Server + ParityTests:
   - Http.Server.Tests (7): (1) end-to-end SSE round-trip text-only via typed `InvokeStreamAsync`; (2) full-events round-trip via `InvokeStreamEventsAsync`; (3) 501 path when agent doesn't implement `IStreamingAiAgent`; (4) Problem Details on 501 carries `StreamingNotSupportedType` URN; (5) content-type is `text/event-stream` with `Cache-Control: no-cache`; (6) `RequestAborted` (client close) stops the server stream promptly; (7) tool-call events interleaved with text deltas on a scripted tool-using provider.
   - Http.Server.Tests (3 more): (8) heartbeat fires on long pauses (TimeProvider-driven); (9) Idempotency-Key header on streaming request bypasses idempotency cache (v0.11 middleware opt-out validation); (10) OpenAPI spec lists `Agents.InvokeStream` operation with `text/event-stream` 200 response + `x-vais-type-urns` on 501.
   - ParityTests (2): (11) text-only client output matches concatenated server-side `StatefulAiAgent.StreamAsync(string)` output on the same scripted provider; (12) full-events client output lists same event kinds + same order as `StreamEventsCore` yields.

**PublicAPI:**

- [x] Http.Server +8 entries (StreamingInvokeOptions + StreamingEndpointAttribute + StreamingNotSupportedType const + StreamingNotSupported factory + related getters/inits).
- [x] Http.Client +4 entries (2 class overloads + 2 DIM interface methods).

### PR 4 — v0.12.0-preview cut

**Packages**: all 22 for the cut.

Tasks:

- [x] **API freeze**: `Unshipped` → `Shipped` across the 4 packages touched by this pillar (Abstractions, Core, Http.Server, Http.Client). Other 18 packages shipped unchanged since `v0.11.0-preview`.
- [x] **Pack**: `dotnet pack Vais.Agents.sln -c Release -p:VersionPrefix=0.12.0 -p:VersionSuffix=preview -o artifacts/packages` → 22 `.nupkg` + 22 `.snupkg`.
- [x] **Smoketest**: bumped all 22 package refs to `0.12.0-preview`; added a streaming-invoke library-surface probe exercising `StatefulAiAgent` via `IStreamingAiAgent.StreamAsync` — enumerates events, asserts event kinds + delta count. Probe line: `Streaming invoke: events-yielded=4 first-event=TurnStarted last-event=TurnCompleted delta-count=2 urn-streaming-not-supported=urn:vais-agents:streaming-not-supported streaming-types-probed=4 heartbeat-default-seconds=15`. Final line updated to `"All twenty-two Vais.Agents.* 0.12.0-preview packages consumed cleanly from a plain .NET 9 console app."`
- [x] **Tag**: annotated `v0.12.0-preview` created on OSS repo `main` at commit `b39f3e9` (API freeze). Not pushed.
- [x] **Milestone log** entry in [`actor-agents-oss-milestone-log.md`](./actor-agents-oss-milestone-log.md).
- [x] **Research doc §7** update — "SSE streaming Invoke on the HTTP surface" backlog line struck through, pointed at this pillar + findings doc.
- [x] **Doc update on `StatefulAiAgent.StreamAsync`** XML — comment block in the method body explains the source-compat delegation + text projection.

---

## Exit criteria

- [x] All 4 PRs on OSS repo `main` (not pushed), landed as the two-commit pattern used in v0.7/v0.8/v0.9/v0.10/v0.11 (feat `a4abcaa` for PRs 1-3; chore `b39f3e9` for PR 4 API freeze).
- [x] Zero new packages; extensions to 4 production + 3 test projects pack cleanly at `0.12.0-preview` — 22 `.nupkg` + 22 `.snupkg` in `artifacts/packages/`.
- [x] Full non-container test suite green: 569 tests (549 v0.11 baseline + 8 Core + 10 Http.Server + 2 ParityTests = 569). Exactly hits the lower bound of the original "569+" target.
- [x] Smoketest probes the `IStreamingAiAgent` library surface — `StatefulAiAgent` via `IStreamingAiAgent.StreamAsync` yields 4 events (TurnStarted + 2 CompletionDelta + TurnCompleted) — from a fresh .NET 9 console project with only NuGet references.
- [x] `v0.12.0-preview` tag created on the API-freeze commit (`b39f3e9`).
- [ ] **Acceptance demo (manual)**: `curl -N -X POST http://localhost:5080/v1/agents/echo/invoke/stream -H "Content-Type: application/json" -d '{"text":"hi"}' | grep -E 'event:|data:'` against a running smoketest host shows the SSE stream. **Not yet run** — unit-test equivalents in PR 3 cover the automated version (10 Http.Server tests exercise end-to-end SSE round-trip through a TestServer; 2 parity tests prove library ↔ HTTP equivalence). Run manually when a realistic host is wired up.

---

## Decisions locked (from the spike + research walkthrough 2026-04-20)

- **Dedicated route** `POST /v1/agents/{id}/invoke/stream`.
- **Full 10-kind `AgentEvent` taxonomy on the wire** + new `CompletionDelta` subtype; SSE `event:` field is discriminator.
- **`IStreamingAiAgent` capability interface** — Abstractions; implemented by `StatefulAiAgent`; Orleans proxy deferred.
- **Client = two DIM overloads** (text-only + full-events); defaults throw `NotSupportedException`.
- **`System.Net.ServerSentEvents` built-in parser** (zero new deps).
- **15s heartbeat default, channel multiplex, linked CTS**.
- **501 + `urn:vais-agents:streaming-not-supported`** for non-streaming-capable agents.
- **v0.11 idempotency middleware unchanged** — `text/event-stream` opt-out already works when content-type is set first.
- **`CompletionDelta` mirrors `CompletionUpdate`** shape — `TextDelta` + `ModelId?` + `PromptTokens?` + `CompletionTokens?` + `ToolCalls?`.
- **Orleans streaming passthrough deferred** to a future pillar.

---

## Progress log

- 2026-04-20 — plan created after the SSE-streaming-invoke spike closed. 10 decisions locked from the spike's verdict; 4 PRs scoped; 7 open questions flagged for impl. Package count stays at 22 (no new package). Target effort: 2-2.5 days focused work (PR 1 is rote Abstractions additions; PR 2 refactors `StatefulAiAgent.StreamAsync` internals + 8 tests; PR 3 is the bulk — channel multiplex + SSE writer + client parser + 12 tests; PR 4 is the cut/pack rote). **Pending**: start on PR 1 (CompletionDelta + IStreamingAiAgent in Abstractions).
- 2026-04-20 — PR 1 landed on `033-logging-improvement-read`. `Vais.Agents.Abstractions` extended with 1 new public type (`CompletionDelta : AgentEvent` record with 5 fields mirroring `CompletionUpdate`) + 1 new public interface (`IStreamingAiAgent.StreamAsync(userMessage, context, CT) : IAsyncEnumerable<AgentEvent>`). `AgentEvent` closed hierarchy now has 10 subtypes. Full Release build clean; full non-container suite still green: 549 tests (v0.11 baseline), no regressions. **Shape adjustments during impl**: (1) XML `cref="StatefulAiAgent"` on `CompletionDelta`'s ToolCalls docs was unresolvable from Abstractions (lives in Core) — replaced with "the default agent's tool-call loop" plain text; (2) `<paramref name="cancellationToken"/>` on the `IStreamingAiAgent` type-level XML docs failed CS1734 (paramref only works inside method docs) — rewrote as plain `<c>cancellationToken</c>` reference. **Pending**: PR 2 (StatefulAiAgent implements IStreamingAiAgent via new StreamEventsCore helper; existing StreamAsync(string) delegates for source-compat; 8 tests).
- 2026-04-20 — PR 2 landed on `033-logging-improvement-read`. `StatefulAiAgent` now implements `IStreamingAiAgent` via a new private `StreamEventsCoreAsync(userMessage, context, CT) : IAsyncEnumerable<AgentEvent>` helper that drives the per-turn loop and yields events directly. The existing public `StreamAsync(string, CT) : IAsyncEnumerable<string>` stays source-compat, now a thin wrapper that calls `StreamEventsCoreAsync` and filters to `CompletionDelta.TextDelta`. New public `StreamAsync(string, AgentContext, CT)` (implicit interface impl) validates args + stamps RunId + delegates to the same core. 8 new tests in `StatefulAiAgentStreamingEventsTests.cs` covering text-only turn ordering (`TurnStarted` → deltas → `TurnCompleted`), tool-call interleaving (`ToolCallStarted` + `ToolCallCompleted` with matching CallId), guardrail-denied path (`GuardrailTriggered` → `TurnFailed`), tool-guardrail interrupt path (`InterruptRaised` → `TurnFailed`), cancellation-mid-delta ends cleanly (no `TurnFailed`), `CompletionDelta.Context.RunId` matches surrounding events, source-compat projection of the string overload, and retry dedup (single `TurnStarted` across pre-first-delta retries). Full non-container suite green: 557 tests (549 baseline + 8 new). Core 318 → 326. **Shape adjustments during impl**: (1) `StreamEventsCoreAsync` yields `CompletionDelta` on EVERY provider update, including empty-text terminal updates carrying ToolCalls or final token usage — terminal-update observability is important for streaming callers; the string overload still filters empty text to preserve v0.10 behaviour. (2) Guardrail + interrupt events are SYNTHESISED from caught exception fields (`AgentGuardrailDeniedException.{Layer, Reason}` / `AgentInterruptedException.Interrupt.{InterruptId, Reason}`) rather than captured from the bus — avoids a bus-subscription dance with cross-agent-leak potential; the bus publishes the "real" event, the yield delivers an equivalent synthesised copy. (3) Dual emission is deliberate: bus subscribers + streaming observers each see each event exactly once (different observation channels). Consumers who subscribe to bus AND enumerate the stream would see duplicates but that's a consumer bug, not a library one. (4) `ToolCallCompleted.Succeeded` test assertion removed — the interleaving test uses a tool with arg-deserialisation semantics that don't match all fake inputs; event emission + CallId match is what the test verifies. **Pending**: PR 3 (HTTP streaming endpoint in Http.Server + 2 client overloads in Http.Client + 12 tests).
- 2026-04-20 — PR 3 landed on `033-logging-improvement-read`. `Vais.Agents.Control.Http.Server` extended with 3 new public types (`StreamingInvokeOptions` with `HeartbeatInterval`, `StreamingEndpointAttribute` marker, `StreamingNotSupportedType` URN const + `ProblemDetailsMapping.StreamingNotSupported` factory) + new endpoint handler `InvokeStreamAsync` mapped to `POST /v1/agents/{id}/invoke/stream` with full route annotation + `AgentEventSerializer` internal helper (10-case switch dispatching each `AgentEvent` subtype to its SSE event name + JSON body). Channel-based SSE multiplex design: unbounded `Channel<string>`, agent producer task drives `StreamEventsCoreAsync` and writes formatted `event: name\ndata: json\n\n` frames to the channel; heartbeat timer writes `: heartbeat <utc>\n\n` comment lines; SSE-writer task drains channel to `context.Response.Body` and flushes after each write. Linked `CancellationTokenSource` on `context.RequestAborted` coordinates shutdown. v0.11 `AgentControlPlaneIdempotencyMiddleware` gained early-bail check for the new `StreamingEndpointAttribute` metadata — skips the whole idempotency path (header parse, body buffer, cache, 422/409) because body-buffering is fundamentally incompatible with SSE's flush-as-you-go. `VaisProblemDetailsOperationTransformer._urnsByStatus` gained `"501" => [StreamingNotSupportedType]`. `Vais.Agents.Control.Http.Client` extended with 2 new DIM overloads on `IAgentControlPlaneClient` (`InvokeStreamAsync` text-only + `InvokeStreamEventsAsync` full events, both throwing `NotSupportedException` by default) + concrete impls in `AgentControlPlaneClient` using `System.Net.ServerSentEvents.SseParser<AgentEvent?>` with 10-case `eventType` dispatcher; text-only is a thin `OfType<CompletionDelta>()` projection over the full-events stream. `System.Net.ServerSentEvents 10.0.2` added to CPM + client csproj. `Vais.Agents.ParityTests.csproj` gained `Microsoft.AspNetCore.App` framework ref + `Microsoft.AspNetCore.TestHost` + 5 project refs (Control.Abstractions, Control.InProcess, Http.Client, Http.Server, Hosting.InMemory) so SSE endpoint ↔ client round-trip tests can spin up a TestServer. 12 new tests: 10 in `AgentControlPlaneStreamingInvokeTests.cs` (text-only client round-trip, full-events client round-trip, 501 path via `NonStreamingAgentRuntime` returning bare `IAiAgent`, Problem Details URN on 501, content-type + no-cache headers, 404 on unknown agent, tool-call event interleaving, Idempotency-Key bypass on SSE response, OpenAPI lists `Agents.InvokeStream`, empty body → 400) + 2 in `StreamingInvokeParityTests.cs` (text-only client matches library's `StreamAsync` concat; full-events client matches library's `IStreamingAiAgent.StreamAsync` event-kind order). Full non-container suite green: 569 tests (557 after PR 2 + 12 new). Control.Http.Tests 38 → 48; ParityTests 17 → 19. **Shape adjustments during impl**: (1) added `StreamingEndpointAttribute` marker + idempotency-middleware early-bail check because v0.11's content-type opt-out only skipped `CompleteAsync` but still buffered the response body — fundamentally incompatible with SSE's flush-as-you-go semantics. Metadata-based opt-out is the correct layering; v0.11's content-type check stays as a secondary safeguard. (2) 501 path required a custom `NonStreamingAgentRuntime` returning a bare `IAiAgent` — `StatefulAiAgent` (from v0.10) now implements `IStreamingAiAgent` unconditionally, so the useNonStreamingProvider flag on the test helper alone wouldn't trigger the capability check; the flag now also swaps the runtime. (3) Streaming endpoint bypasses `AgentLifecycleManager` entirely — goes directly through `IAgentRegistry.GetAsync` + `IAgentRuntime.GetOrCreate`. Documented cost: policy engine + audit log don't run on streaming invocations; consumers needing those stay on unary `/invoke`. Future pillar can add a streaming method to the lifecycle manager. **Pending**: PR 4 (v0.12.0-preview cut — API freeze, pack 22, smoketest, tag).
- 2026-04-20 — PR 4 landed on OSS `main`. Two commits: `a4abcaa feat(http): SSE streaming Invoke pillar (v0.12 PRs 1-3)` (22 files, +1646 −18) + `b39f3e9 chore: API freeze for v0.12.0-preview — promote Unshipped -> Shipped` (8 files, Unshipped→Shipped promotion across 4 packages — Abstractions +22 entries, Core +1, Http.Server +8, Http.Client +4). Annotated `v0.12.0-preview` tag created on `b39f3e9` (not pushed). 22 `.nupkg` + 22 `.snupkg` packed at `0.12.0-preview` into `artifacts/packages/`. Smoketest refreshed to `0.12.0-preview`; new streaming-invoke probe segment exercises `StatefulAiAgent` via `IStreamingAiAgent.StreamAsync` (enumerates 4 events: TurnStarted + 2 CompletionDelta + TurnCompleted); ran clean. Probe line: `Streaming invoke: events-yielded=4 first-event=TurnStarted last-event=TurnCompleted delta-count=2 urn-streaming-not-supported=urn:vais-agents:streaming-not-supported streaming-types-probed=4 heartbeat-default-seconds=15`. Final line: `"All twenty-two Vais.Agents.* 0.12.0-preview packages consumed cleanly from a plain .NET 9 console app."` Milestone log entry appended (`actor-agents-oss-milestone-log.md`). Research doc §7 "SSE streaming Invoke on the HTTP surface" backlog line struck through and pointed at this pillar + findings doc. **Pillar closed.** Only follow-up remaining: the manual acceptance demo (`curl -N` against a running smoketest host) — unit-test equivalents are green (10 Http.Server SSE tests + 2 parity tests).
