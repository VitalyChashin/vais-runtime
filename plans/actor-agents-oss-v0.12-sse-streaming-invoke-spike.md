# v0.12 SSE streaming Invoke on HTTP surface — research spike

Scoped research pass before committing to a v0.12 pillar plan. Companion to [`actor-agents-oss-extraction-research.md`](./actor-agents-oss-extraction-research.md) §7 backlog: *"SSE streaming Invoke on the HTTP surface (wire format + event taxonomy already specified in the v0.6 HTTP-API design doc; server/client impl deferred)."* Picks up where v0.6 PR 3 stopped (unary Invoke only) and where v0.10 landed the streaming-filter pipeline at the library level. Targets the same two control-plane HTTP packages (`Vais.Agents.Control.Http.Server` + `Vais.Agents.Control.Http.Client`). Created 2026-04-20.

---

## Why a spike before a pillar

The v0.6 plan specified "SSE streaming Invoke — text deltas" (one terse line on `POST /v1/agents/{id}/invoke/stream`) and deferred the implementation. Since then:

- **v0.10 shipped the library-level streaming story**: `StatefulAiAgent.StreamAsync(string)` returns `IAsyncEnumerable<string>` with full tool-calling loop + filter chain + retry boundary. Streaming-filter surface (`IStreamingAgentFilter`) is mature.
- **v0.4 shipped `AgentEvent` + `IAgentEventBus`**: closed-hierarchy semantic events (`TurnStarted` / `ToolCallStarted` / `ToolCallCompleted` / `GuardrailTriggered` / `InterruptRaised` / `TurnCompleted` / `TurnFailed`). Already Orleans-wire-tested in M3e-3a/3b.
- **v0.11 shipped idempotency middleware** with a `text/event-stream` opt-out check already in place (`AgentControlPlaneIdempotencyMiddleware.cs:184`).
- **v0.10 deferral noted**: `OrleansAiAgentProxy` doesn't proxy `StreamAsync`. Streaming works only on `InMemoryAgentRuntime`-hosted agents today.

The design space therefore has more options than the v0.6 plan's "text deltas" implied — whether to emit the full event taxonomy on SSE, how to attach at the server side without breaking the shipped `IAgentLifecycleManager` surface, how the client exposes text-only vs. full-events consumers. Each choice is costly to reverse post-freeze. A focused 1-day spike settles scope before we burn pillar-length time.

Spike output: findings doc + archetype consumer sketches. No public surface change, no package bumps, no tag.

---

## Current state (confirmed before spike)

Verified as of 2026-04-20 (`v0.11.0-preview` on OSS `main`):

- **Server** (`Vais.Agents.Control.Http.Server`): 7 unary REST verbs under `/v1`. No streaming route. `AgentControlPlaneIdempotencyMiddleware.cs:184` already has `contentType.StartsWith("text/event-stream")` opt-out.
- **Client** (`Vais.Agents.Control.Http.Client`): 8 methods (Create/List/Query/Update/Cancel/Evict/Invoke/Signal) + v0.11 idempotency overloads. No streaming method.
- **Library** (`Vais.Agents.Core`): `StatefulAiAgent.StreamAsync(string userMessage, CancellationToken) : IAsyncEnumerable<string>` on the concrete class (`StatefulAiAgent.cs:356`). **Not** on the `IAiAgent` interface. Provider check throws `InvalidOperationException` when the injected provider doesn't implement `IStreamingCompletionProvider` (both SK + MAF adapters do).
- **Events** (`Vais.Agents.Abstractions`): `AgentEvent` abstract record + 9 concrete subtypes (`TurnStarted`, `TurnCompleted`, `TurnFailed`, `ToolCallStarted`, `ToolCallCompleted`, `GuardrailTriggered`, `InterruptRaised`, `HandoffRequested`, `ToolCallReplayed`). All records with `[DateTimeOffset At, AgentContext Context]` positional base.
- **Event bus** (`IAgentEventBus`): `PublishAsync(AgentEvent)` + `Subscribe(Func<AgentEvent, ValueTask>) : IDisposable`. In-memory + Orleans impls shipped.
- **Orleans gap**: `IAiAgent` has no `StreamAsync`; `OrleansAiAgentProxy` has no streaming passthrough. Documented deferral from v0.10.

---

## Five blocking questions

1. **Q1 — Route shape.** Three candidates: (a) dedicated route `POST /v1/agents/{id}/invoke/stream` (v0.6 plan); (b) content-negotiation on existing `POST /v1/agents/{id}/invoke` via `Accept: text/event-stream`; (c) query-param opt-in `?stream=true`. Dedicated route wins on OpenAPI clarity + natural `text/event-stream` content-type so idempotency middleware opts out without consumer effort. Lean: **(a) dedicated route** — v0.6 plan stands.

2. **Q2 — Event taxonomy on the wire.** Biggest scope question. Four options:
   - **(a) Text deltas only** (v0.6 plan's "text deltas"). `event: delta\ndata: "hello "\n\n`. Discards tool-call visibility.
   - **(b) Text deltas + terminal envelope**: `delta` events + one `completed` (usage tokens + modelId) or `error`. Minimal observability.
   - **(c) Full `AgentEvent` taxonomy**: emit `turn.started` / `tool.started` / `tool.completed` / `guardrail.triggered` / `delta` (text) / `turn.completed` / `turn.failed` / `interrupt.raised`. Consumers get tool-call visibility.
   - **(d) Mirror Anthropic/OpenAI SSE shape**: `message_start` / `content_block_delta` / `message_delta` / `message_stop`. Familiar, but introduces a third shape.

   Lean: **(c)** — use our own `AgentEvent` taxonomy (shipped + already JSON-serialisable + wire-tested). Text deltas ride as a new `delta` SSE event type carrying the `CompletionUpdate` shape. Consumers who want "just text" filter to `delta`.

3. **Q3 — Server-side attachment model.** `IAgentLifecycleManager.InvokeAsync` is unary; `StatefulAiAgent.StreamAsync` is on the concrete class; `IAiAgent` doesn't expose it. Three paths:
   - **(a) Extend `IAgentLifecycleManager`** with `StreamAsync(handle, request) : IAsyncEnumerable<AgentEvent>`. Adds to a shipped surface — requires DIM default for mocks.
   - **(b) Extend `IAgentRuntime`** with a streaming method; HTTP endpoint uses runtime directly, bypassing lifecycle manager's unary path.
   - **(c) New capability interface** `IStreamingAiAgent` on Abstractions. `StatefulAiAgent` implements it. HTTP endpoint does `is IStreamingAiAgent`. Matches v0.9's `IResumableAgentGraph<TState>` precedent.

   Lean: **(c)** — capability interface keeps `IAgentLifecycleManager` + `IAgentRuntime` source-compatible. Orleans `OrleansAiAgentProxy` doesn't implement in v0.12 → documented limitation; HTTP endpoint returns 501 with `urn:vais-agents:streaming-not-supported`.

4. **Q4 — Client-side shape.** Two candidate overloads on `IAgentControlPlaneClient`:
   - **(a) `InvokeStreamAsync(agentId, request, ...) : IAsyncEnumerable<string>`** — just text deltas; matches v0.6 plan.
   - **(b) `InvokeStreamEventsAsync(...) : IAsyncEnumerable<AgentEvent>`** — full taxonomy.

   Lean: **ship both** — one SSE parse loop internally + a thin text-filter wrapper on (a). Mainstream callers want (a); observability/debugging callers want (b).

5. **Q5 — Integration with idempotency + cancellation + Orleans + heartbeat.**
   - **Idempotency**: middleware's `text/event-stream` check already skips cache-write. Endpoint must set `context.Response.ContentType = "text/event-stream"` *before* `next(context)` — otherwise middleware sees null content-type when capturing. Need to validate the call ordering.
   - **Cancellation**: client closes connection → `context.RequestAborted` fires → agent's `StreamAsync` CT flows downstream → graceful exit. Standard pattern; no new code.
   - **Orleans passthrough**: `OrleansAiAgentProxy` doesn't proxy `StreamAsync`. Deferred in v0.12; server returns 501 + Problem Details URN. Future work.
   - **SSE heartbeats**: long pauses (slow tool call) can look like dead connections to proxies. Standard fix: emit `: heartbeat\n\n` comments every N seconds (15s default). Needs a timer + coordination with the yield loop.

   Lean: set content-type first, trust `RequestAborted` for cancel, 501 on Orleans-backed agents, 15s heartbeat default configurable via options.

---

## Tasks (research + archetype exercises)

- [x] **Q1 — Route shape.** Validated middleware-interaction model: `AgentControlPlaneIdempotencyMiddleware.cs:184` inspects `context.Response.ContentType` AFTER `next(context)` returns; endpoint that sets content-type as first action → middleware correctly opts out. Scored three URL candidates on OpenAPI legibility / middleware compat / consumer surprise. **Outcome**: (a) dedicated route `POST /v1/agents/{id}/invoke/stream`.
- [x] **Q2 — Event taxonomy audit.** Read `AgentEvent.cs` — 9 shipped sealed-record subtypes; XML doc sanctions adding to the closed hierarchy as an Unshipped addition. Sketched a 20-line SSE transcript covering one realistic turn with a tool call; audit output is a 10-row event-name → subtype → field-set table. **Outcome**: (c) full taxonomy + new `CompletionDelta : AgentEvent` (mirrors `CompletionUpdate` shape, rides as `event: delta`).
- [x] **Q3 — Capability interface draft.** Drafted `IStreamingAiAgent.StreamAsync(string userMessage, AgentContext context, CT) : IAsyncEnumerable<AgentEvent>` in Abstractions. `StatefulAiAgent` implements via a new `StreamEventsCore` helper emitting events directly; existing `StreamAsync(string) : IAsyncEnumerable<string>` delegates + projects to `CompletionDelta.TextDelta`. Orleans proxy NOT implementing — HTTP endpoint returns 501 on non-streaming-capable agents. Cancellation via `HttpContext.RequestAborted` → linked CTS → standard.
- [x] **Q4 — Client-side parse loop.** `System.Net.ServerSentEvents 10.0.2` already transitively available (SK pulls it via OpenAI SDK). Sketched `InvokeStreamEventsAsync` using `SseParser<AgentEvent?>.Create` + 10-case switch on `eventType`. Two DIM overloads on `IAgentControlPlaneClient`: `InvokeStreamAsync` (text-only via `OfType<CompletionDelta>()` filter) + `InvokeStreamEventsAsync` (full taxonomy). Zero new deps.
- [x] **Q5 — Heartbeat + cancellation + 501.** Channel-multiplex design: unbounded `Channel<string>`, agent-loop + 15s heartbeat timer both write, single SSE-writer task drains to response body. Linked CTS coordinates shutdown. 501 path with new `urn:vais-agents:streaming-not-supported` URN + `ProblemDetailsMapping.StreamingNotSupported` helper. Content-type set as first action so v0.11 idempotency middleware sees it.
- [x] **Findings doc.** [`actor-agents-oss-v0.12-sse-streaming-invoke-findings.md`](./actor-agents-oss-v0.12-sse-streaming-invoke-findings.md) — Q1–Q5 synthesis + verdict (10 locked decisions + proposed 4-PR pillar shape + 2-2.5-day effort estimate).

---

## Exit criteria

- [x] All five questions answered with evidence (not opinion) — Q1 from middleware-code audit + scored URL candidates; Q2 from `AgentEvent.cs` shape audit + SSE transcript; Q3 from capability-interface draft + cancellation walk-through; Q4 from `System.Net.ServerSentEvents` availability check + 10-case parse-loop sketch; Q5 from channel-multiplex design + 501 Problem Details + content-type ordering.
- [x] Recommendation lands: **ready to write v0.12 pillar plan.** 10 decisions locked in the findings doc.

No public surface change. No package bumps. No tag.

---

## Progress log

- 2026-04-20 — spike plan created after design conversation. Five blocking questions scoped (route shape, event taxonomy, server attachment, client shape, heartbeat + cancellation + Orleans). Lean positions recorded per question going in; spike is to validate or overturn each.
- 2026-04-20 — Spike complete. All five leans held up. Q1: dedicated route `POST /v1/agents/{id}/invoke/stream`; v0.11 idempotency middleware's content-type check already handles SSE opt-out when endpoint sets content-type first. Q2: full `AgentEvent` taxonomy on the wire + new `CompletionDelta : AgentEvent` subtype (10 event kinds total); SSE `event:` field is the discriminator. Q3: `IStreamingAiAgent` capability interface in Abstractions; `StatefulAiAgent` implements via new `StreamEventsCore` helper; Orleans proxy deferred (501 path). Q4: `System.Net.ServerSentEvents` built-in parser (zero new deps, already transitively available); two DIM client overloads (text-only + full-events). Q5: channel-multiplex design with 15s heartbeat timer + linked CTS; new `urn:vais-agents:streaming-not-supported` 501 URN. Findings doc landed with 10 locked decisions + proposed 4-PR pillar shape (PR 1 Abstractions; PR 2 Core impl; PR 3 server endpoint + client overloads; PR 4 v0.12.0-preview cut). Effort estimate: 2-2.5 days. Zero new packages. **Ready to write v0.12 pillar plan.**
