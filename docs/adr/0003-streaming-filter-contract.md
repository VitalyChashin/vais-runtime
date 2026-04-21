# ADR 0003: Streaming filter contract — one DIM method, three override points

- **Status:** Accepted — 2026-04-20 (v0.10)
- **Context bounded by:** Phase 1 of the Vais.Agents library — streaming-filter pipeline pillar.
- **Supersedes:** the v0.4.1 "streams bypass filters + Polly" limitation documented in `execution-loop.md`.

## Context

`StatefulAiAgent.StreamAsync` shipped in v0.4.1 with a well-known gap: `IAgentFilter` (the synchronous request→response chain) did not apply, and `ResiliencePipeline` (Polly) wasn't wired either. Streaming-aware filters were deferred to "after we see what consumers need."

By v0.10 three consumer demands had surfaced consistently:

1. **Around-provider hook.** Wrap the entire `StreamAsync` call — short-circuit early, rewrite the request before it hits the provider, observe the stream as a whole.
2. **Per-delta transform.** Inspect or modify each `CompletionUpdate` inline — scrub PII from deltas before yielding, emit per-chunk telemetry, pre-emit gating.
3. **Post-drain validation.** Run checks on the accumulated `CompletionResponse` after the stream completes — output guardrails are the canonical example, but consumer-authored analyzers fit too.

A naive shape would ship three separate interfaces — `IStreamingRequestFilter`, `IStreamingDeltaFilter`, `IStreamingCompleteFilter` — each with its own DI registration list and dispatcher wrapper. Every host would wire three filter collections instead of one. Filters that want all three hooks (Langfuse enrichment being the canonical case) register three times and correlate them by hand.

## Decision

1. **Single interface, three methods.** `IStreamingAgentFilter` in `Vais.Agents.Abstractions` exposes `InvokeAsync(request, next, ct) : IAsyncEnumerable<CompletionUpdate>` + `OnStreamDeltaAsync(update, ct) : ValueTask<CompletionUpdate>` + `OnStreamCompleteAsync(final, ct) : ValueTask`. All three carry default pass-through implementations — filters override what they need.

2. **`InvokeAsync` is the DIM backbone.** Around-provider invocation follows the standard ASP.NET Core middleware shape: `(request, next, ct) → stream`. A filter that wants to rewrite the request before downstream calls the `next` delegate with a modified request. A filter that wants to short-circuit the provider returns an `IAsyncEnumerable` built from local data without calling `next`. The shape is identical to `IAgentFilter.InvokeAsync` (non-streaming) — muscle memory transfers.

3. **Chain executes in registration order.** `StatefulAgentOptions.StreamingFilters` is an `IReadOnlyList<IStreamingAgentFilter>` that wraps the provider bottom-up: the last filter in the list sits innermost, closest to `ICompletionProvider.StreamAsync`. `OnStreamDeltaAsync` fires per-delta in the same registration order — one delta passes through filter 0, then filter 1, etc, before being yielded to the consumer. `OnStreamCompleteAsync` fires once at end-of-stream, before output guardrails.

4. **Filter-domain exceptions exempt from resilience.** The Polly `StreamingResiliencePipeline` wraps the provider + filter chain, but filter-thrown exceptions (subtypes of `FilterAbortedException`) bypass retry — the filter is doing deliberate policy work; retrying would paper over the signal.

5. **Phase 1 retry + Phase 2 drain.** `StreamingResiliencePipeline` applies retries only **before** the first `CompletionUpdate` is yielded — the "Phase 1" window covering enumerator-open + first `MoveNextAsync`. Once a delta has been observed by the consumer, retries stop (retrying mid-stream replays already-committed content); the stream "drains" through the try/finally that disposes the enumerator on whatever happens next. Polly's `ResiliencePipeline` handles Phase 1; Phase 2 is an inline try/finally in `StatefulAiAgent.StreamEventsCoreAsync`.

6. **Filters ship as options-bag state, not DI.** `StatefulAgentOptions.StreamingFilters` holds the filter chain per-agent. DI registration is on the consumer's side — they `sp.GetServices<IStreamingAgentFilter>()` and assign to options at agent-construction time. Keeps the agent itself framework-neutral; no `IServiceProvider` leaks into `Vais.Agents.Core`.

## Why not …

| Option | Why rejected |
|---|---|
| Three separate filter interfaces (request, delta, complete) | Filters that want all three (Langfuse, PII scrubbing, usage accounting) register three times and correlate manually. Higher API surface for identical operational shape. |
| Extend `IAgentFilter` with streaming variants via default methods | `IAgentFilter.InvokeAsync` returns `Task<CompletionResponse>`, not `IAsyncEnumerable<CompletionUpdate>`. Conflating the two means every `IAgentFilter` caller has to branch on "is this a stream?" — worse ergonomics for the non-streaming 80%. |
| Retry on mid-stream failure (replay deltas from the top) | Semantically wrong: the consumer has already committed to the first deltas. A retry would repeat content, double-charge tokens on the retry path, and look like a different response to downstream aggregators. Phase 2 drain + rethrow is the honest shape. |
| Skip `StreamingResiliencePipeline` entirely; let filters retry | Polly is the lingua franca of .NET resilience. Consumers already know it; redirecting them to custom filter code means re-learning retry semantics for a single feature. |
| Ship `OnStreamStartAsync` as a fourth hook | Redundant with the `InvokeAsync` around-provider wrapper — a filter that wants "at-start" logic runs it before calling `next`. Avoided surface bloat. |

## Consequences

- **Positive:** One DI registration shape for all streaming-filter needs. A filter wiring all three hooks is one class, one registration, one lifecycle.
- **Positive:** Polly semantics ported cleanly. `StreamingResiliencePipeline` is a sibling of the existing `ResiliencePipeline`; consumers who already configured the non-streaming one know what to do.
- **Positive:** Closes the v0.4.1 "filters bypassed on streaming" gap without introducing a second filter surface.
- **Negative:** "Phase 2 drain — no retry after first delta" is a subtle rule that surprises callers familiar with Polly's retry-the-whole-operation semantics. Documented in [stream-with-tools.md](../guides/stream-with-tools.md) and in `StreamingResiliencePipeline` XML docs.
- **Negative:** Filters that throw late (during `OnStreamDeltaAsync` on the 47th delta) produce partial output — the consumer sees 46 deltas then an exception. Consumer code must be defensive about mid-stream throws. We document this under "Things that catch people" in the guide.

## Follow-ups

- **v0.11 idempotency middleware** wraps the streaming route with `[StreamingEndpoint]` — which opts out of body-fingerprint buffering. Filter-level idempotency (per-delta replay detection) stays consumer-authored; no first-class support planned.
- **v0.12 SSE wire** takes filter output → event-stream directly; filters see in-process `CompletionUpdate`, clients see SSE-serialised `CompletionDelta`. No double-serialisation round-trip inside the filter chain.
- **Orleans grain streaming** (deferred to a later pillar) will need the filter chain to run inside the grain, not on the orchestrating client. Design open; `IStreamingAgentFilter` is likely to ship as-is with a new `IGrainStreamingFilter` parallel if grain-scoped metadata (e.g. grain identity, activation count) become needed.
