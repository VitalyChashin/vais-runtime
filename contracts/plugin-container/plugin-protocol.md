# Container Plugin Protocol

**Version:** 0.26  
**Status:** Frozen — breaking-change boundary. Changes require a version bump and coordinated update to the SDK (IP-2) and runtime shim (IP-3). v0.25 adds the 502/503/504 error codes additively over 0.24; v0.26 adds the optional `context.renewTokenUrl` field for session-mode token renewal (additive — absent for short-turn plugins). The runtime accepts 0.24–0.26 and older plugins need no change.

---

## Overview

Endpoints exposed by each plugin container. Called by the runtime's `ContainerAgentShim`. All endpoints are HTTP/1.1 or HTTP/2; `Content-Type: application/json` unless noted.

The runtime identifies the container via the `vais.plugin.handlerTypeName` OCI image label, validated against the `handlerTypeName` field in `/v1/metadata` at startup.

---

## Message type

Used in request and response bodies throughout this protocol and in `gateway-internal.md`.

```json
{
  "role": "system | user | assistant | tool",
  "content": "string | null",
  "toolCalls": [
    {
      "id": "call_abc123",
      "name": "web_search",
      "arguments": { "query": "climate change 2025" }
    }
  ],
  "toolCallId": "string | null"
}
```

Field rules:

| Role | `content` | `toolCalls` | `toolCallId` |
|---|---|---|---|
| `system` | required string | omitted | omitted |
| `user` | required string | omitted | omitted |
| `assistant` | string or null (null when turn is tool-call only) | optional list | omitted |
| `tool` | required string (tool result) | omitted | required |

- `toolCalls` is omitted (not null) when empty.
- `arguments` is a JSON object, not a serialised string. The SDK serialises to/from the LLM's wire format.

**Alignment:** This shape is a strict subset of `Microsoft.Extensions.AI`'s `ChatMessage`. Python and C# SDKs pass it directly to their LLM clients without transformation. The C# runtime uses `ChatTurn` internally and maps at the container boundary.

---

## `GET /v1/metadata`

Returns plugin identity and capability declaration. Called once at startup by `ContainerPluginHostService`.

**Response:**
```json
{
  "handlerTypeName": "sgr_analyst.agent.AnalystAgent",
  "targetApiVersion": "0.24",
  "capabilities": ["invoke", "stream"],
  "sdkVersion": "0.1.0"
}
```

Field rules:
- `handlerTypeName` — must match the `vais.plugin.handlerTypeName` image label exactly. Mismatch is a startup error.
- `targetApiVersion` — `major.minor` string. `ContainerPluginHostService` validates against the runtime's supported range with a lexicographic string comparison (no strict semver parsing today; `"0.24"` / `"1.0"` / `"99.99"` all work as long as they sort within range). Rejects plugins outside the range with `urn:vais:container:abi-failed`.
- `capabilities` — `"invoke"` is required. `"stream"` is optional; if absent, `ContainerAgentShim` falls back to non-streaming invoke. Capability enforcement on `/v1/stream` is currently advisory — see `/v1/stream` below.
- `sdkVersion` — informational; logged at startup.

---

## `POST /v1/invoke`

Synchronous invocation. Returns the complete response after all LLM and tool calls have finished.

### Request

```json
{
  "agentId": "sgr-analyst",
  "sessionId": "550e8400-e29b-41d4-a716-446655440000",
  "messages": [ "<Message>", "..." ],
  "llmGatewayUrl":  "http://runtime-internal:5001/v1/llm",
  "toolGatewayUrl": "http://runtime-internal:5001/v1/tools",
  "opaqueState": { "key": "value" },
  "timeoutSeconds": 60,
  "context": {
    "traceparent": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
    "runId": "3c7c4a28-78a2-4b68-b12f-2c8e6f1a9d11",
    "correlationId": "req-abc",
    "callToken": "<opaque bearer token>",
    "renewTokenUrl": "http://runtime-internal:5001/v1/container-gateway/token/renew"
  }
}
```

Field rules:
- `messages` — assembled by the preprocessing pipeline (IP-4: `IAgentPreprocessor` chain — `HistoryAssembler` at order 0, `SystemPromptInjector` at order 10, plus any consumer-registered preprocessors). Complete context including system prompt and history. The container passes this list directly to its LLM call.
- `llmGatewayUrl` / `toolGatewayUrl` — the container **must** use these URLs for all LLM and tool calls. Hard-coded LLM calls not using `llmGatewayUrl` violate architectural principle P4. The SDK provides clients pre-configured with these URLs; plugin authors use `request.llm` and `request.tools`, not raw HTTP clients.
- `opaqueState` — JSON object or `null`. Null on first invocation or after a fresh-start (see error types). The container must not assume this field is present. **Size cap:** not currently enforced on the container plugin path (the 1 MiB cap on `PythonPluginLoaderOptions.MaxAgentStateSizeBytes` applies to Python subprocess plugins, not container plugins). Plan defensively for ≤1 MiB until enforcement is added.
- `timeoutSeconds` — applies to the entire invocation including nested LLM and tool calls. The container is responsible for respecting this budget.
- `context.callToken` — must be included as `Authorization: Bearer <callToken>` on every callback to `llmGatewayUrl` and `toolGatewayUrl`. For short-turn plugins the validity window is `timeoutSeconds + 30s` (clock skew). For session-mode plugins (manifest `spec.sessionTtlSeconds`) it is short and renewable — see `context.renewTokenUrl`. Algorithm and key rotation are specified in IP-3. (Separate from the OTLP-receiver token, which uses a 24 h TTL.)
- `context.renewTokenUrl` — session mode only (v0.26); absent for short-turn plugins. Absolute URL the plugin POSTs to (current token as the bearer) for a fresh short-lived token before the current one expires. The SDK's gateway clients renew transparently; see `gateway-internal.md §POST /v1/container-gateway/token/renew`.

### Response

```json
{
  "assistantMessage": "Based on the research...",
  "opaqueState": { "key": "updated-value" },
  "journal": [
    {
      "toolName": "web_search",
      "toolCallId": "call_abc123",
      "inputJson": "{\"query\":\"climate\"}",
      "outputJson": "{\"results\":[...]}"
    }
  ],
  "usage": {
    "inputTokens": 1200,
    "outputTokens": 450,
    "cachedTokens": 200
  }
}
```

Field rules:
- `assistantMessage` — the final assistant reply text. Required.
- `opaqueState` — updated plugin-private state, or `null` if unchanged. The grain stores whatever is returned here; `null` means "no change."
- `journal` — tool calls made during this invocation, in execution order. Optional. `inputJson` and `outputJson` are serialised strings of the argument and result objects.
- `usage` — token counts for the invocation. `cachedTokens` is 0 if the provider does not report cache hits.

---

## `POST /v1/stream`

Same request shape as `POST /v1/invoke`. Response: `Content-Type: text/event-stream`.

SSE event taxonomy (aligns with ADR 0004 conventions):

| `event:` | `data:` payload | When |
|---|---|---|
| `delta` | `{ "text": "partial response..." }` | Each text chunk from the LLM |
| `tool.started` | `{ "toolName": "web_search", "toolCallId": "call_abc" }` | Before a tool call |
| `tool.completed` | `{ "toolName": "web_search", "toolCallId": "call_abc", "outputJson": "..." }` | After a tool call |
| `done` | full `InvokeResponse` object (see `/v1/invoke` response above) | Terminal event — always last |
| `error` | `{ "errorType": "...", "errorMessage": "..." }` | Before `done` on error |
| `: heartbeat` | *(SSE comment, no `data:` field)* | Every 15s if no other event |

Rules:
- `done` is always emitted, even on error. On error, `done` carries `null` for `assistantMessage`; the error shape is signalled by a preceding `error` event.
- If the container does not declare `"stream"` in `/v1/metadata` capabilities, this endpoint **should** return `404` so `ContainerAgentShim` falls back to `/v1/invoke`. Today this is advisory — both shipped SDKs (.NET, Python) implement the endpoint unconditionally, and the runtime tolerates any non-2xx as "stream unavailable, fall back". Tightening to a hard MUST is tracked alongside the broader SDK-level capability enforcement work.

---

## `GET /health`

```json
{ "status": "ready" }
```

- HTTP 200 when the plugin server has completed initialisation and is ready to accept `/v1/invoke` calls.
- HTTP 503 (recommended) at any other time. The shipped .NET and Python SDKs only implement the 200 path today, leaving the 503 contract for plugin authors to implement manually if their plugin has a startup window worth signalling distinctly. `ContainerPluginHostService` tolerates any non-2xx response as "not ready yet" — it polls with retry until `startupTimeoutSeconds` elapses.

`ContainerPluginHostService` polls this endpoint at startup and after container restart. Configurable timeout via `spec.startupTimeoutSeconds` (default 30).

---

## Error response

Any HTTP 4xx/5xx from `/v1/invoke` or `/v1/stream` must carry:

```json
{
  "errorType": "OpaqueStateDeserializationError | InternalError | LlmGatewayError | ToolError | Timeout",
  "errorMessage": "Full exception message with stack trace if available.",
  "diagnosticTail": "Trailing portion of container stderr / internal log."
}
```

`diagnosticTail` is a free-form string. The runtime truncates whatever the container returns to ~500 chars before logging — earlier drafts said "last 20 lines" but the actual cap is byte-based; the runtime does not parse line counts. Containers should emit the most recent ~20 log lines and stay under ~500 chars per line where possible.

### HTTP status → `errorType` mapping

| HTTP status | `errorType` | Grain behaviour |
|---|---|---|
| 422 | `OpaqueStateDeserializationError` | Clear `OpaqueState`; retry with `opaqueState: null` (fresh-start) |
| 500 | `InternalError` | Propagate; fail the graph node. **Terminal** — not retried even when the node declares a `retryPolicy` (a plugin code bug should not loop). |
| 502 | `LlmGatewayError` | Propagate; **retryable** under the graph node's `retryPolicy`. |
| 503 | `ToolError` | Propagate; **retryable** under the graph node's `retryPolicy`. |
| 504 | `Timeout` | Propagate; **retryable** under the graph node's `retryPolicy`. Aborts the in-flight invocation only — the container is not drained or restarted. |

Any other 4xx / 5xx returned by the container is treated as `InternalError` by `ContainerAgentShim`.

`retryPolicy` is the per-node retry policy on the graph node (`GraphNodeRetryPolicy`: `maxAttempts`, `initialBackoffSeconds`, `backoffMultiplier`, `maxBackoffSeconds`). With no `retryPolicy`, every status above propagates after a single attempt. The plugin's `errorType` is preserved in `GraphFailed.ErrorType` (P9), so retry, alerting, and telemetry can distinguish the failure class instead of seeing every failure as `InternalError`.

The SDKs emit these codes both automatically and on demand: the LLM gateway client raises `LlmGatewayError` on an upstream non-2xx, the tool gateway client raises `ToolError` when a tool call cannot be dispatched (a tool that *runs* and returns an error result is handed back to the plugin so the agent loop can continue), and the SDK raises `Timeout` when `timeoutSeconds` elapses. Plugin authors may also raise any of them directly — `LlmGatewayError`/`ToolError`/`Timeout` in the Python `vais-plugin` SDK, `LlmGatewayException`/`ToolException`/`PluginTimeoutException` in the .NET `Vais.Plugin.Sdk`.

### Shim error-handling contract

`ContainerAgentShim.InvokeAsync` must:

1. Detect any non-2xx HTTP response.
2. Attempt to deserialise the error body; if the body is not valid JSON or is empty, synthesise `InternalError` with `errorMessage: "Container returned non-JSON error body (HTTP <status>)"` and `diagnosticTail: <raw body truncated to 500 chars>`.
3. Log at ERROR with `run_id`, `agent_id`, `errorType`, and `diagnosticTail` before re-throwing.
4. Include `diagnosticTail` in the exception message so it reaches `GraphFailed.ErrorMessage`.

The exact structured catch/log implementation is specified in IP-3.
