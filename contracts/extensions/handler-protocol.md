# Extension Handler Protocol

**Version:** 0.35  
**Status:** Frozen — breaking-change boundary. Changes require a version bump and coordinated update to the language SDKs and runtime proxy.

---

## Overview

Endpoints each container extension exposes. Called by the runtime's `HttpContainerHandlerProxy` family from `Vais.Agents.Runtime.Extensions.Container`.

The runtime identifies the container via the manifest `spec.image` field, validated against the `extensionId` field returned by `GET /v1/handlers` at startup.

---

## `GET /v1/handlers`

Called once at startup by `ContainerExtensionLifecycleManager`. Advertises the handler set this container implements.

**Response:**

```json
{
  "extensionId": "vais-ext-mem0",
  "version": "0.3.1",
  "targetApiVersion": "0.30",
  "handlers": [
    {
      "id": "load",
      "seam": "agentInput",
      "preEndpoint":  "/handlers/load/pre",
      "postEndpoint": "/handlers/load/post"
    },
    {
      "id": "extract",
      "seam": "agentOutput",
      "preEndpoint":  "/handlers/extract/pre",
      "postEndpoint": "/handlers/extract/post"
    }
  ]
}
```

The lifecycle manager validates the advertised set against the manifest's `spec.handlers[].id` set; a mismatch fails the lifecycle with `urn:vais:extension/handler-mismatch`.

---

## Paired endpoints (per handler)

### `POST /handlers/<id>/pre`

Called immediately before the wrapped operation (`next()` in middleware shape). The body carries the seam-specific context object and a per-call `callId`.

**Request:**

```json
{
  "callId": "550e8400e29b41d4a716446655440000",
  "context": { /* seam-specific — see §Per-seam contexts */ }
}
```

**Response:**

```json
{
  "action": "next" | "mutate" | "shortCircuit",
  "continuationToken": "<opaque, echoed to /post>",
  "contextPatch": { /* key-value pairs applied to context.properties when action=mutate */ }
}
```

Action semantics:

- `next` — proceed to the wrapped operation, then call `/post`.
- `mutate` — apply `contextPatch` to `context.Properties`, then proceed to the wrapped operation, then call `/post`.
- `shortCircuit` — do not call the wrapped operation. The chain stops. The `/post` endpoint is NOT called.

### `POST /handlers/<id>/post`

Called after the wrapped operation completes (only when `/pre` returned `next` or `mutate`).

**Request:**

```json
{
  "callId": "550e8400e29b41d4a716446655440000",
  "continuationToken": "<from /pre response>"
}
```

**Response:**

```json
{
  "action": "passThrough" | "mutate",
  "contextPatch": { /* key-value pairs applied to context.properties when action=mutate */ }
}
```

---

## Per-seam contexts

### `agentInput`

Pre-request `context` shape:

```json
{
  "agentId": "my-agent",
  "runId": "run-abc123",
  "nodeId": null,
  "message": "What is the capital of France?"
}
```

Fields match `AgentInputContext` (minus the mutable `Properties` bag — see §Wire constraints).

### `agentOutput`

Pre-request `context` shape:

```json
{
  "agentId": "my-agent",
  "runId": "run-abc123",
  "sessionId": "sess-xyz",
  "outputTokens": 42,
  "inputTokens": 100
}
```

Fields match `AgentOutputContext` (minus `RequestMessages`, `ResponseMessage`, `Properties` — see §Wire constraints).

### `toolGatewayMiddleware`

Warm, per-tool-call. The pre/post **responses** differ from the input/output seams: they carry the
tool outcome (`result`/`error`), not a `contextPatch`.

Pre-request `context` shape:

```json
{
  "toolName": "shell",
  "callId": "call-abc",
  "arguments": { "cmd": "ls" },
  "agentId": "my-agent",
  "runId": "run-abc123",
  "privilegeLevel": "Standard",
  "workspaceId": "ws-1",
  "allowedTools": ["shell", "search"]
}
```

Pre-response:

```json
{
  "action": "next" | "shortCircuit",
  "continuationToken": "<opaque>",
  "result": "<shortCircuit: tool result returned to the agent>",
  "error":  "<shortCircuit: deny/error string>"
}
```

`shortCircuit` returns the synthesized outcome (`result`/`error`) without dispatching the tool.
Arguments cannot be rewritten into the tool — the runtime dispatches the original request.

Post-request carries the produced outcome:

```json
{ "callId": "call-abc", "continuationToken": "<from pre>", "outcomeResult": "...", "outcomeError": null }
```

Post-response (`mutate` replaces the outcome — e.g. redact the result):

```json
{ "action": "next" | "mutate", "result": "<replacement result>", "error": "<replacement error>" }
```

### `llmGatewayMiddleware`

Hot, per-turn — a `host: container` handler on this seam requires the apply-time latency
acknowledgment (`X-Vais-Accept-Latency-Cost: true`). **Non-streaming only**; streaming LLM calls
bypass container handlers. The pre envelope field is `request` (not `context`).

Pre-request:

```json
{
  "callId": "call-llm",
  "request": {
    "messages": [{ "role": "user", "content": "hi", "toolCalls": null, "toolCallId": null }],
    "systemPrompt": "...",
    "temperature": 0.2,
    "maxTokens": 512,
    "tools": [{ "name": "search", "description": "...", "parametersSchema": {} }],
    "responseFormat": { "schema": {}, "name": "R", "strict": true },
    "agentId": "",
    "runId": null
  }
}
```

`tools` are read-only — an `ITool` cannot round-trip back into a mutated request. `agentId`/`runId`
are emitted empty (a completion request carries no run identity at the proxy).

Pre-response:

```json
{
  "action": "next" | "shortCircuit" | "mutate",
  "continuationToken": "<opaque>",
  "response": { "text": "...", "promptTokens": null, "completionTokens": null },
  "request":  { "...": "mutate: replacement request, same shape as the pre-request 'request'; tools ignored" }
}
```

`shortCircuit` returns `response` without calling the model; `mutate` rewrites the request from
`request` (messages / params / responseFormat only).

Post-request carries the model response:

```json
{ "callId": "call-llm", "continuationToken": "<from pre>", "response": { "text": "...", "promptTokens": 100, "completionTokens": 42 } }
```

Post-response (`mutate` replaces the response — e.g. redact the text):

```json
{ "action": "next" | "mutate", "response": { "text": "<replacement>", "promptTokens": null, "completionTokens": null } }
```

### `errorInterceptor`

A **single-call hook**, not a `next`-wrapping pair. Fired once when an agent turn or graph node
fails, on the failure path. There is no `/post` — the runtime posts only to `/handlers/<id>/pre`
(the advertised `postEndpoint` is never called for this seam). The body uses the standard `context`
envelope.

The handler may observe the failure and optionally rewrite the user-facing message. **It cannot
break diagnosability (P9):** the failure still propagates, `errorType` is immutable, and the
structured ERROR log (with run/node id + stack) is written *before* the interceptor runs. Only the
human-facing message is replaceable.

Pre-request:

```json
{
  "callId": "call-err",
  "context": {
    "agentId": "research-agent",
    "runId": "run-123",
    "nodeId": "plan",
    "errorType": "System.TimeoutException",
    "errorMessage": "raw 504 from upstream"
  }
}
```

`nodeId` is set when the failure is inside a graph node; null for a plain agent turn. `runId` is
null for a stateless turn.

Pre-response:

```json
{ "message": "A transient error occurred. Please retry. (ref: run-123)" }
```

A non-empty `message` replaces the surfaced error message; `null` (or absent) observes only. The
runtime never reads `errorType` back from the response — it is fixed.

### `graphNode`

Hot, per-node — a `host: container` handler on this seam requires the apply-time latency
acknowledgment (`X-Vais-Accept-Latency-Cost: true`). Wraps a graph node's **body** execution (Agent
and Code kinds; control nodes are not wrapped). The pre/post pair carries the node's output state.

Pre-request:

```json
{
  "callId": "call-node",
  "context": {
    "runId": "run-123",
    "nodeId": "plan",
    "nodeKind": "Agent",
    "agentId": "research-planner",
    "superStep": 2,
    "input": { "topic": "..." }
  }
}
```

`agentId` is empty for `Code` nodes. `input` is the node's binding-filtered input state (its keys are
domain state keys, preserved verbatim).

Pre-response:

```json
{
  "action": "next" | "shortCircuit",
  "continuationToken": "<opaque>",
  "output": { "...": "shortCircuit: substitute node output, merged as if the node ran" }
}
```

`shortCircuit` returns `output` as the node's result **without running the node body** (the cache /
deny path); the `/post` endpoint is NOT called. The substitute output flows through the runtime's
normal state-merge + per-step checkpoint, so a short-circuited node is journaled exactly like a real
run — **resume stays consistent and P2 (one-node-one-turn) holds**.

Post-request carries the body output:

```json
{ "callId": "call-node", "continuationToken": "<from pre>", "output": { "lastAssistantText": "..." } }
```

Post-response (`mutate` replaces the node output before it merges into graph state):

```json
{ "action": "next" | "mutate", "output": { "...": "replacement node output" } }
```

### `sessionLifecycle`

A **single fire-and-forget notification**, not a `next`-wrapping pair. Fired when an agent session
opens (grain activation) and closes (grain deactivation). The runtime posts only to
`/handlers/<id>/pre` (the advertised `postEndpoint` is never called); the response body is ignored
(observe-only). Cold seam — no latency gate.

Pre-request:

```json
{
  "callId": "call-sess",
  "context": {
    "agentId": "research-planner",
    "sessionId": "run-123",
    "phase": "opened" | "closing",
    "turnCount": 4,
    "history": [ { "role": "user", "text": "..." }, { "role": "assistant", "text": "..." } ]
  }
}
```

`history` is populated only on `closing` (null on `opened`) and carries role+text per turn for
summarize-on-close. **Best-effort (P1):** `closing` runs on idle-timeout, shutdown, or explicit
session removal, but a hard crash skips it — so summarize-on-close is lossy. Because grains
deactivate/reactivate across idle cycles, a long session may produce multiple `opened`/`closing`
pairs (the phases mean *grain activated / grain deactivating*). A handler failure never aborts the
session lifecycle.

Pre-response: ignored (any 2xx). The runtime does not read a body.

---

## Wire constraints

The wire shapes intentionally exclude:

| Excluded field | Reason |
|---|---|
| `AgentInputContext.Properties` | `IDictionary<string, object?>` is not round-trip stable across JSON serialization. Extensions add properties via the `contextPatch` mechanism. |
| `AgentOutputContext.RequestMessages` / `ResponseMessage` | Large `ChatTurn` lists; not needed for the common middleware use case. Opt-in via a future protocol extension. |
| `AgentOutputContext.Properties` | Same as AgentInputContext.Properties above. |

---

## Authentication

Extensions receive a per-call `callToken` in the context's `context.properties["vais.callToken"]` field (Phase C). The container must echo it on any callback to `/v1/container-gateway/*` endpoints. Token validity: `invokeTimeoutSeconds + 30s`.

In Phase B, authentication is not enforced (runtime and extensions share a private network by default via `VAIS_DOCKER_PLUGIN_NETWORK`).

---

## Trace context

The runtime injects W3C trace context and Vais run-identity headers on every `/pre` and `/post` request so that spans emitted by the extension container appear as children of the runtime's `vais.extension.handler.invoke` span.

### Request headers

| Header | MUST/MAY | Description |
|---|---|---|
| `traceparent` | MUST | W3C `traceparent` header; value is the invocation span's context. |
| `tracestate` | MAY | W3C `tracestate` header when present in the ambient trace context. |
| `X-Vais-Agent-Id` | MUST | The `agentId` of the agent this invocation is for. |
| `X-Vais-Run-Id` | MAY | The `runId` when the invocation is part of a durable run; absent for stateless calls. |
| `X-Vais-Node-Id` | MAY | The `nodeId` when invoked inside a graph node; absent for agent-level (non-graph) seams. |

### Extension-emitted spans

Container extensions MAY emit their own child spans. When doing so:

- Use the Python SDK helper `vais_extension.telemetry.extract_parent_context(headers)` (Python) or `DistributedContextPropagator.Current.Extract()` (C#) to restore the parent context from the request headers before starting spans.
- Export spans to the runtime OTLP receiver via the `OTEL_EXPORTER_OTLP_ENDPOINT` environment variable. For runtime-managed containers the runtime injects this variable automatically. The URL format is `http://<runtime-host>:5001/v1/otlp/v1/traces?source=extension&id=<extension-id>`. The `source` and `id` query parameters cause the receiver to tag forwarded spans with `vais.span.source=extension_otlp` and `vais.extension.id=<extension-id>`.
- Set `OTEL_SERVICE_NAME=vais-extension/<extension-id>` so the resource appears correctly in Langfuse / Grafana Tempo.
- Do NOT include `agent_id` in custom metric labels — record agent context on spans only, not metric series.

Response headers MUST NOT include `traceparent` — the runtime owns the parent context.

---

## Failure modes

| Condition | HTTP status | Recovery |
|---|---|---|
| `GET /v1/handlers` unreachable | n/a | Lifecycle marks extension Failed; returns `urn:vais:extension/handler-discovery-failed` |
| `GET /v1/handlers` handler set mismatch | n/a | Lifecycle fails; returns `urn:vais:extension/handler-mismatch` |
| `POST /handlers/<id>/pre` timeout or 5xx | 504 / 5xx | Runtime applies handler's `failureMode` — `fail` aborts turn, `skip` logs WARN + proceeds to `next()` |
| `POST /handlers/<id>/post` failure | any | Runtime logs WARN; swallows error (post is best-effort) |
| `continuationToken` absent in `/post` call | n/a | Treated as empty token; extension should handle gracefully |

---

## Version history

| Version | Date | Change |
|---|---|---|
| 0.35 | 2026-05-24 | Added `sessionLifecycle` seam — a single fire-and-forget (`/pre` only) notification fired on session open (grain activate) + close (grain deactivate). The `closing` context carries the conversation history for summarize-on-close. Cold seam; best-effort (P1); grain-cycle scoped. Additive — earlier seams remain compatible. |
| 0.34 | 2026-05-24 | Added `graphNode` seam — a per-node `/pre` + `/post` hook wrapping graph node body execution. `pre` may short-circuit with a substitute `output` (journaled like a real run, resume-safe); `post` may transform the node `output`. Hot seam (per-node). Additive — earlier seams remain compatible. |
| 0.33 | 2026-05-23 | Added `errorInterceptor` seam — a single-call (`/pre` only) failure hook that observes and optionally rewrites the user-facing message without changing `errorType` or suppressing the failure (P9). Additive — earlier seams remain compatible. |
| 0.32 | 2026-05-23 | Added `toolGatewayMiddleware` + `llmGatewayMiddleware` seam contexts and their outcome/response-carrying pre/post shapes. Additive — `agentInput`/`agentOutput` extensions targeting 0.30/0.31 remain compatible. |
| 0.31 | 2026-05-20 | Added §Trace context: `traceparent`, `X-Vais-*` headers, OTLP URL discriminator format |
| 0.30 | 2026-05-19 | Initial extension handler protocol (Phase B) |
