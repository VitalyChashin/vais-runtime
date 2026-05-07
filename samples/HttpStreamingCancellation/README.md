# HttpStreamingCancellation

Cancel an in-flight SSE stream cleanly — demonstrates Ctrl-C semantics. A slow provider emits 30 deltas over ~600ms; the client cancels after 350ms and receives an `OperationCanceledException`.

## Run

```bash
dotnet run --project samples/HttpStreamingCancellation
```

## Expected output

```
Server: http://127.0.0.1:<port>

== streaming (will cancel after ~350ms) ==
1 2 3 4 5 6 7 
[cancelled] received 7 deltas before cancellation

Done.
```

*(Delta count varies ±2 depending on scheduling and machine speed; typical range: 5–12.)*

## What it demonstrates

- `CancellationTokenSource` timeout — `InvokeStreamEventsAsync(..., cts.Token)` propagates cancellation to both the SSE read loop and the underlying `HttpClient` request.
- Server-side clean stop — the channel-based SSE writer observes `HttpContext.RequestAborted` when the connection closes; no further SSE frames are written after the client disconnects.
- `OperationCanceledException` propagation — the `IAsyncEnumerable<AgentEvent>` throws on the next `MoveNextAsync` when the token is signalled; the `await foreach` exits cleanly via the `catch (OperationCanceledException)` block.
- Provider-level respect — `SlowStreamingProvider` calls `ct.ThrowIfCancellationRequested()` at the top of each loop iteration so the server-side agent turn also stops yielding.

## Docs

- [Stream invocations over HTTP](../../docs/guides/stream-invocations-over-http.md)
- [`HttpStreamingInvoke`](../HttpStreamingInvoke) — same setup without cancellation
