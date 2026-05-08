# Internal Gateway Protocol

**Version:** 0.24  
**Status:** Frozen — breaking-change boundary. Changes require a version bump and coordinated update to the SDK (IP-2) and runtime shim (IP-3).

---

## Overview

Endpoints the runtime exposes for container → runtime callbacks. Called by plugin containers via the gateway clients provided in `InvokeRequest` (`llmGatewayUrl`, `toolGatewayUrl`).

**Network binding:** Internal port (default 5001), separate from the Kubernetes Service's external-facing port. Network policy restricts access to plugin container CIDRs only.

**Authentication:** Every request must carry `Authorization: Bearer <callToken>`, where `callToken` is the value from `InvokeRequest.context.callToken`. Token generation algorithm and key rotation are specified in IP-3. Token validity window: `timeoutSeconds + 30s` to allow for clock skew.

**Required headers on all requests:**

```
Authorization: Bearer <callToken>
X-Agent-Id: <agentId>
X-Run-Id: <runId>
traceparent: <traceparent from InvokeRequest.context>
```

---

## `POST /v1/llm/complete`

LLM completion callback. Routes through the full `ILlmGateway` middleware chain — token accounting, Langfuse tracing, and rate limiting apply exactly as for direct agent invocations.

### Request

```json
{
  "messages": [ "<Message>", "..." ],
  "modelId": "claude-sonnet-4-6",
  "options": {
    "temperature": 0.7,
    "maxTokens": 4096
  }
}
```

Field rules:
- `messages` — full conversation history including system prompt. Uses the `Message` type from `plugin-protocol.md`.
- `modelId` — optional; null means "use the gateway's configured default for this agent."
- `options` — optional; null means "use defaults." Full parameter set is specified in the `ILlmGateway` contract, not here.

### Response (non-streaming)

```json
{
  "message": "<Message (role: assistant)>",
  "usage": {
    "inputTokens": 1200,
    "outputTokens": 450,
    "cachedTokens": 200
  }
}
```

### Streaming

`Accept: text/event-stream` → SSE with `delta` / `done` events (same taxonomy as `/v1/stream` in `plugin-protocol.md`). The container SDK client pipes these deltas through its own `/v1/stream` SSE response to the caller.

### Gateway behaviour

- `callToken` is validated; `X-Agent-Id` and `X-Run-Id` are used to reconstruct `AgentContext` for `GatewayEventMiddleware`.
- A completion call from inside the container produces a `vais_gateway_events` row with the correct `agent_id` and `run_id`.
- `callToken` security algorithm and validation are specified in IP-3.

---

## `POST /v1/tools/invoke`

Tool invocation callback. Routes through the `IToolGateway` middleware chain. Same `callToken` validation as `/v1/llm/complete`.

### Request

```json
{
  "toolName": "web_search",
  "toolCallId": "call_abc123",
  "arguments": { "query": "climate change 2025" }
}
```

Field rules:
- `toolCallId` — the same ID from the `toolCalls` entry in the assistant message. Round-tripped back in the response for correlation.
- `arguments` — JSON object (not a serialised string). Consistent with the MCP gateway tool invocation format (D7).

### Response

```json
{
  "toolCallId": "call_abc123",
  "content": "Search results: ...",
  "isError": false
}
```

Field rules:
- `content` — string result. Structured results from MCP are serialised to a string at the gateway boundary.
- `isError` — if `true`, `content` carries the error description. The container should add this as a `role: tool` message and continue the LLM loop, allowing the LLM to react to the tool error.
