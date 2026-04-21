# ADR 0004: SSE streams carry the full `AgentEvent` taxonomy, not text-only deltas

- **Status:** Accepted — 2026-04-20 (v0.12)
- **Context bounded by:** Phase 1 of the Vais.Agents library — SSE streaming-invoke pillar.
- **Supersedes:** no prior decision — v0.12 is the first HTTP streaming commitment.

## Context

`POST /v1/agents/{id}/invoke/stream` needs a wire format. The SSE-with-text-deltas pattern is ubiquitous — OpenAI, Anthropic, Cohere, xAI, Together all stream assistant text as `data:` frames carrying text chunks, with a terminal `[DONE]` sentinel. A client reading the raw stream can reconstruct the full reply by concatenating `data:` fields.

That shape works beautifully for chat completion, but Vais.Agents runs agents: tool dispatches, guardrail decisions, HITL interrupts, handoffs, token-usage updates, `ToolCallReplayed` cache hits from the idempotency journal. If the wire format carries only text deltas, every non-text event becomes either **invisible** to the HTTP client or **second-class** — shoved into a sideband event bus that only same-process observers can subscribe to. An HTTP-only consumer (a CLI, a React frontend, a cross-language service) would have to poll a separate "events" endpoint after the stream to reconstruct the run.

Three consumer scenarios drove this decision:

1. **Dashboards.** A realtime dashboard wants to surface "tool X running" while the assistant text is still streaming. Text-only deltas can't express this.
2. **Audit + replay.** Compliance needs the full `AgentEvent` trace per HTTP request — guardrail decisions, principals, tool invocations. Emitting these over a separate channel breaks the single-session-single-trace model.
3. **Interrupt-driven UIs.** When a guardrail interrupts mid-stream, the browser needs to see the `InterruptRaised` event to render the HITL approval prompt. Flooding that information through a side channel is worse ergonomics than getting it inline.

Two alternative shapes considered:

- **Text-only deltas + side-channel WebSocket for events.** Two connections per run; reconnect discipline duplicated; ordering across the two channels is the consumer's problem.
- **`event: data` only, subtype in the JSON body.** Wire parser has one dispatch path but every consumer has to parse JSON + branch on a discriminator inside — worse ergonomics than SSE's native `event:` field.

## Decision

1. **Every `AgentEvent` subtype gets a stable wire-event name.** Ten subtypes in v0.12: `turn.started`, `turn.completed`, `turn.failed`, `tool.started`, `tool.completed`, `tool.replayed`, `guardrail.triggered`, `interrupt.raised`, `handoff.requested`, `delta`. The SSE `event:` field carries the name; the `data:` field carries JSON for the subclass body. Adding a new `AgentEvent` subtype requires adding a wire-name entry (an **unshipped** edit to `AgentEventSerializer`).

2. **`CompletionDelta` is the text-carrying event.** Text deltas ride as `event: delta` — same mechanism as all other events. No privileged position, no special casing. `CompletionDelta.TextDelta` is non-null (may be empty); `ModelId` / `PromptTokens` / `CompletionTokens` / `ToolCalls` populate on terminal updates. The consumer filters for `delta` events if it only wants text (client's `InvokeStreamAsync` does exactly this).

3. **Wire names are kebab-case with dot separators.** Matches common SSE conventions (Server-Sent-Events spec uses free-form event names; GitHub, Stripe, Mapbox all use dot-separated lowercase). No `AgentEventSubtype` enum lives on the wire — the string is the contract. Renaming a wire name is a **breaking change** and requires a major-version bump on the HTTP surface.

4. **Heartbeat comments keep the connection warm.** SSE comment lines (`: heartbeat <utc>`) at the `StreamingInvokeOptions.HeartbeatInterval` cadence (15s default). Comments don't dispatch to the client's event handler — they land in the transport layer. Upstream proxies, load balancers, and CDNs see traffic and keep the socket open.

5. **The `CompletionDelta` subtype lives in `Vais.Agents.Abstractions`, not the wire serialiser.** It's a first-class `AgentEvent` — in-process event-bus subscribers observe it exactly like a `ToolCallStarted`. The wire format is downstream of the event taxonomy, not upstream. If we ever swap SSE for something else (long-polling JSON lines, HTTP/3 push, custom binary framing), the event records stay untouched.

6. **Client shapes match the taxonomy.** `InvokeStreamEventsAsync` yields `IAsyncEnumerable<AgentEvent>` — the client side uses `System.Net.ServerSentEvents` to parse frames + `JsonSerializer.Deserialize` keyed on the wire event name, returning `AgentEvent` subclass instances. `InvokeStreamAsync` is a projection helper that filters for `CompletionDelta` and yields `TextDelta` strings — two calls, one wire format.

## Why not …

| Option | Why rejected |
|---|---|
| OpenAI-shape text-only deltas + separate events endpoint | Two connections to reason about; ordering ambiguity; reconnect discipline duplicated; worse ergonomics for HTTP-only consumers. |
| Single `event: data` with subtype discriminator in JSON | Loses SSE's native event-routing — every consumer writes a switch statement on a payload field. Verbose + error-prone. |
| `Last-Event-ID` resumable streams | Out of scope for v0.12. Resumable streaming means checkpointing event IDs + storing partial run state server-side + replay semantics for `CompletionDelta` (which is generated, not persistent). Deferred. |
| Binary framing (gRPC-web, MessagePack over HTTP) | SSE works through every HTTP intermediate; binary framing breaks behind proxies that lack grpc-web support. SSE is the universal minimum. |
| Drop `tool.replayed` from the wire (it's a cache hit, not a run event) | Audit + observability consumers explicitly asked for it — "did the run re-execute the tool or replay the journal?" is a common question. Cheaper to emit than to re-derive. |

## Consequences

- **Positive:** One connection per invocation carries everything. Dashboards, audit log, interrupt UIs all consume the same stream. Single reconnect semantics; single cancellation token.
- **Positive:** In-process consumers (same event bus, same `StatefulAiAgent.StreamAsync` direct caller) see identical events to HTTP consumers. No dual implementation.
- **Positive:** Adding a new event subtype is additive on both sides — add the record in Abstractions, add a wire-name mapping in the serialiser, update the wire-name table in `events.md`. No format migration.
- **Negative:** SSE `data:` fields carry JSON, which doubles the byte count vs. raw text deltas. Per-delta overhead is ~150 bytes of framing around 5-30 bytes of content — real cost for high-throughput scenarios. Consumers that need pure-text throughput use `InvokeStreamAsync` (which filters + discards non-delta events server-side? no, client-side — the server still emits everything).
- **Negative:** Mid-stream connection drop loses events with no resume. Hitting drop → retry replays the whole run from the top with a fresh `Idempotency-Key`. Acceptable for v0.12; revisit when a concrete resumable-stream use case surfaces.
- **Negative:** Wire names are strings — a typo is runtime-only. Mitigated by centralising in `AgentEventSerializer` + a round-trip parser test (`ParityTests`) that exercises every subtype.

## Follow-ups

- **Orleans-silo streaming passthrough** is deferred in v0.12 — grain-hosted agents return `501 urn:vais-agents:streaming-not-supported`. The grain surface will need an `IAsyncEnumerable<AgentEvent>` method in a later pillar; the wire format stays identical (SSE, same event names).
- **Resumable streams** — if demand surfaces, `Last-Event-ID` plus a server-side event-journal grain keyed by `runId` is a reasonable extension. The event taxonomy doesn't need to change; only the connection lifecycle does.
- **Binary framing as a transport option** — if scale demands it, ship `?wire=json-seq` or `?wire=msgpack` as a query-string toggle. The event shape stays the same.
