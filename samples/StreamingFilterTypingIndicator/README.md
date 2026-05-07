# StreamingFilterTypingIndicator

Wrap the streaming turn with a typing-indicator filter — observe all three `IStreamingAgentFilter` hooks in order: the around-provider `InvokeAsync`, the per-delta `OnStreamDeltaAsync`, and the end-of-stream `OnStreamCompleteAsync`.

## Run

```bash
dotnet run --project samples/StreamingFilterTypingIndicator
```

## Expected output

```
[InvokeAsync] provider starting — around-provider hook
·Agentic ·AI ·combines ·large ·language ·models ·with ·planning ·loops ·memory ·and ·tools ·to ·accomplish ·complex ·multi-step ·tasks ·with ·minimal ·human ·intervention. 
[InvokeAsync] provider stream drained

[OnStreamCompleteAsync] 21 deltas, 151 chars accumulated
```

*(The `·` before each word is printed by `OnStreamDeltaAsync` — it fires before the delta reaches the outer consumer.)*

## What it demonstrates

- `IStreamingAgentFilter.InvokeAsync` — around-provider hook. `Console.WriteLine("[InvokeAsync] provider starting")` fires before the first delta; the `provider stream drained` banner fires after the last. The inner `await foreach (next(...))` is where the provider is actually run.
- `IStreamingAgentFilter.OnStreamDeltaAsync` — per-delta hook. Called by the agent harness on every filter (in registration order) **before** the delta is yielded to the outer consumer. Returns the delta unchanged here; PII-scrubbing, throttling, or truncation would happen here.
- `IStreamingAgentFilter.OnStreamCompleteAsync` — end-of-stream hook. Called once, after the accumulator drains, before output guardrails. Receives the full accumulated `CompletionResponse`.
- `StatefulAgentOptions.StreamingFilters` — ordered chain. Multiple filters compose: f1.InvokeAsync wraps f2.InvokeAsync wraps … the provider. `OnStreamDeltaAsync` and `OnStreamCompleteAsync` fire on every filter in registration order.

## Docs

- [Streaming filters](../../docs/concepts/streaming-filters.md)
- [`HelloStreaming`](../HelloStreaming) — basic streaming without filters
- [`HelloStreamingTools`](../HelloStreamingTools) — streaming with tool calls
