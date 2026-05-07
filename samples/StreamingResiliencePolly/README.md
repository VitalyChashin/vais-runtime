# StreamingResiliencePolly

Add Polly-backed resilience to `StreamAsync` without breaking already-yielded deltas. The `StreamingResiliencePipeline` retries the provider call only before the first delta is produced; once content starts flowing, failures propagate to the caller.

## Run

```bash
dotnet run --project samples/StreamingResiliencePolly
```

## Expected output

```
Starting streaming turn...
stream:   [polly] retry #1: transient failure on attempt 1
  [polly] retry #2: transient failure on attempt 2
Success on attempt 3.

Total provider calls: 3 (expected 3)
```

*(The retry log lines appear before any text — retries fire before the first delta is yielded.)*

## What it demonstrates

- `StatefulAgentOptions.StreamingResiliencePipeline` — accepts any Polly `ResiliencePipeline`. The agent wraps the streaming provider call with it before iteration begins.
- Pre-first-delta retry contract — the pipeline only applies before the first `CompletionUpdate` is yielded. After any delta escapes to the caller the stream is considered committed and retries are disabled (avoids duplicate delivery of partial text).
- `ResiliencePipelineBuilder.AddRetry` — Polly 8 retry API with `ShouldHandle`, `MaxRetryAttempts`, `Delay`, and `OnRetry` callback.
- Custom `TransientException` — `ShouldHandle = new PredicateBuilder().Handle<TransientException>()` gates retries on the specific type; domain exceptions (guardrail denials, budget exceeded) are not retried regardless of the pipeline.

## Docs

- [Resilience](../../docs/concepts/resilience.md)
- [`StreamingFilterTypingIndicator`](../StreamingFilterTypingIndicator) — streaming filter pipeline
