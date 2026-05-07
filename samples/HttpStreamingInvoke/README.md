# HttpStreamingInvoke

End-to-end SSE streaming over the HTTP control plane. An in-process server exposes `POST /v1/agents/{id}/invoke/stream`; a co-located `AgentControlPlaneClient` consumes the SSE event stream and prints every `AgentEvent`.

## Run

```bash
dotnet run --project samples/HttpStreamingInvoke
```

## Expected output

```
Server: http://127.0.0.1:<port>

== InvokeStreamEventsAsync ==
  TurnStarted
  Delta        "Streaming "
  Delta        "delivers "
  Delta        "agent "
  Delta        "responses "
  Delta        "token "
  Delta        "by "
  Delta        "token "
  Delta        "as "
  Delta        "they "
  Delta        "are "
  Delta        "generated. "
  Delta        ""
  TurnCompleted  tokens=10+9

Done.
```

## What it demonstrates

- `AddAgentControlPlane()` + `MapAgentControlPlane("/v1")` — register and mount the HTTP control-plane surface on an in-process ASP.NET Core `WebApplication`.
- `POST /v1/agents/{id}/invoke/stream` — SSE endpoint; emits the full `AgentEvent` taxonomy: `TurnStarted` → per-delta `CompletionDelta` → terminal `TurnCompleted` or `TurnFailed`.
- `AgentControlPlaneClient.InvokeStreamEventsAsync` — SSE consumer that yields `IAsyncEnumerable<AgentEvent>`; parses the `text/event-stream` response using `System.Net.ServerSentEvents.SseParser`.
- `IStreamingAiAgent` guard — the server returns `501 urn:vais-agents:streaming-not-supported` when the runtime agent doesn't implement `IStreamingAiAgent`; `StatefulAiAgent` with a streaming provider passes automatically.

## Docs

- [Stream invocations over HTTP](../../docs/guides/stream-invocations-over-http.md)
- [`HttpStreamingCancellation`](../HttpStreamingCancellation) — cancel a mid-flight SSE stream
- [`HelloStreaming`](../HelloStreaming) — library-layer streaming without HTTP
