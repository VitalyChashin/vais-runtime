# HttpIdempotencyInMemory

Wire the v0.11 idempotency middleware; retry the same call and see `Idempotency-Replayed: true`. Two identical `POST /v1/agents/{id}/invoke` requests with the same `Idempotency-Key` are sent; the second is served from the in-memory store without re-invoking the agent.

## Run

```bash
dotnet run --project samples/HttpIdempotencyInMemory
```

## Expected output

```
Server: http://127.0.0.1:<port>

== call 1 — first invocation ==
  status:    200
  replayed:  false
  body:      {"text":"Echo reply from the scripted agent.","sessionId":null,"metadata":null}

== call 2 — same key + same body (expect replay) ==
  status:    200
  replayed:  true
  body:      {"text":"Echo reply from the scripted agent.","sessionId":null,"metadata":null}

bodies match: True
agent invoke count: 1 (expected 1)
```

## What it demonstrates

- `AddAgentControlPlaneIdempotency()` — registers `InMemoryIdempotencyStore` + `IdempotencyOptions` in DI.
- `app.UseAgentControlPlaneIdempotency()` — mounts `AgentControlPlaneIdempotencyMiddleware` in the pipeline. Must be called before endpoint dispatch.
- `Idempotency-Key` request header — client attaches a stable key to the POST; the middleware fingerprints the request body and stores the first response.
- `Idempotency-Replayed: false` / `true` response headers — the server stamps every response with the replay status. Callers check this header to distinguish a fresh execution from a cached replay.
- Agent invoke count = 1 — the scripted provider is called only once; the second HTTP call returns the cached JSON without running the agent.

## Idempotency matrix

| Scenario | Store status | HTTP response |
|---|---|---|
| First call with key | `New` | Executes handler; caches 2xx/4xx response |
| Retry with same key + same body | `Replay` | Returns cached response; `Idempotency-Replayed: true` |
| Retry with same key + different body | `Mismatch` | `422 urn:vais-agents:idempotency-mismatch` |
| Concurrent call with same in-flight key | `InFlight` | `409 urn:vais-agents:idempotency-in-flight` + `Retry-After: 1` |

## Docs

- [Idempotency](../../docs/concepts/idempotency.md)
- [`HttpStreamingInvoke`](../HttpStreamingInvoke) — invoke without idempotency middleware
