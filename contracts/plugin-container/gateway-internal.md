# Internal Gateway Protocol

**Version:** 0.26  
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

---

## `POST /v1/sections/build`

Section pipeline callback (v0.26). Reuses the named agent's runtime-side section composition (history reducer → composer → `IContextProvider` chain → resolver) and returns the typed `Section[]` the plugin would otherwise have to derive from `InvokeRequest.messages`. The packer is **not** run — the plugin decides which sections to include in its own LLM call. Same `callToken` validation and required-headers regime as `/v1/llm/complete`.

This endpoint is purely additive — default plugin behaviour (consume `InvokeRequest.messages`) is unchanged. Plugins opt in by calling `/v1/sections/build` and then either flattening with a shipped adapter (`vais_plugin.adapters.openai`) or mapping sections into a framework-native layout (LangGraph state slots, LangChain `ChatPromptTemplate` parts, etc.).

### Request

```json
{
  "messages": [ "<Message>", "..." ]
}
```

Field rules:
- `messages` — the plugin's current view of the conversation (typically just `InvokeRequest.messages` echoed back, or whatever the plugin wants the providers to treat as the candidate). Uses the `Message` type from `plugin-protocol.md`. Empty is allowed and produces empty turn-shaped sections.

The plugin owns the conversation state on this path; the runtime owns provider composition. A future `suppress: [...]` allowlist may be added without a breaking bump (additive optional field).

### Response

```json
{
  "sections": [
    {
      "id": "system.persona",
      "kind": "SystemSegment",
      "payload": { "value": "You are a research assistant…" },
      "order": 0,
      "producerId": "PersonaContributor",
      "budget": { "priority": 5 }
    },
    {
      "id": "retrieval.docs",
      "kind": "SystemSegment",
      "payload": { "value": "Source 1: …\nSource 2: …" },
      "producerId": "KnowledgeRetrievalContextProvider",
      "budget": { "priority": 5 }
    },
    {
      "id": "history.window.0",
      "kind": "UserMessage",
      "payload": { "turn": { "role": "user", "content": "What's our return policy?" } },
      "producerId": "SessionHistory"
    }
  ],
  "totalChars": 482
}
```

Field rules:
- `sections` — resolver-ordered list. Empty when the agent has no producers wired.
- `sections[].id` — hierarchical, validated against `[a-zA-Z0-9._-]`, dots permitted as namespace separators. See `Vais.Agents.SectionId.Validate`.
- `sections[].kind` — one of `SystemSegment`, `UserMessage`, `AssistantMessage`, `ToolMessage`, `ToolDeclaration`, `ResponseFormat`, `Metadata`. The kind dictates the `payload` shape (see table below).
- `sections[].payload` — typed per kind:
  | Kind | Payload shape |
  |---|---|
  | `SystemSegment` | `{ "value": "<text>" }` |
  | `UserMessage` / `AssistantMessage` / `ToolMessage` | `{ "turn": <Message> }` (Message type from `plugin-protocol.md`) |
  | `ToolDeclaration` | `{ "tools": [ { "name": "...", "description": "...", "parametersSchema": { ... } }, ... ] }` |
  | `ResponseFormat` | `{ "spec": { "schema": { ... }, "name": "...", "strict": true } }` |
  | `Metadata` | `{ "values": { "<key>": <value>, ... } }` — never flattens to the wire; observability only |
- `sections[].order` — optional integer. Explicit-order sections sort first within their kind (ascending); null-order sections cluster at the end in registration order.
- `sections[].producerId` — optional. Conventionally the producer's type name (e.g. `PersonaContributor`, `KnowledgeRetrievalContextProvider`). Required for per-section observability attribution.
- `sections[].budget` — optional `{ "priority": int, "maxChars": int? }`. Priority 0 = critical, 10 = drop first; default is 5 when omitted. Plugins that build their own request typically ignore `budget` — it's informative, not enforced on this path (the packer doesn't run).
- `totalChars` — sum of character lengths across all sections that carry text (`SystemSegment`, turn-shaped, `ToolDeclaration` summaries, `ResponseFormat` schema raw text). `Metadata` sections contribute 0. The plugin uses this for budget-aware adapter rendering.

### Gateway behaviour

- `callToken`, `X-Agent-Id`, `X-Run-Id` validated and used to reconstruct `AgentContext` for the per-turn `IContextProvider` invocation — providers see the same context they would in a runtime-hosted agent.
- The `messages` payload is converted to a candidate `CompletionRequest` (the same shape the runtime's `StatefulAiAgent` would assemble). Providers receive this candidate via `ContextInvocationContext.Candidate` — so retrieval producers can read the last user turn for their query, history-shaped sections reflect the plugin's view, etc.
- The pipeline runs `composer.ComposeSectionsAsync → IContextProvider.InvokeAsync (per registered provider) → ISectionResolver.ResolveAsync`. Packer + telemetry emitter are **not** run; section telemetry sinks (`vais_request_section_*` metrics, `RequestSectionsBuilt` events) for plugin-served sections come from the plugin's subsequent `/v1/llm/complete` call, not this endpoint. The history reducer is also skipped — the plugin already owns history shape.
- Producer exceptions propagate as HTTP 500 with a JSON `{ "error": "...", "producerId": "..." }` body. Plugins should treat this as a recoverable error and fall back to `InvokeRequest.messages`.

### Plugin-shaped sections (deferred)

Some plugins build their own context (LangGraph state, persistent agent threads, framework-internal memory) and want to suppress runtime-emitted sections. Two future directions:

1. **Manifest-declared override** — `spec.sections.suppress: [memory.*, history.window]` on the agent manifest, with the runtime skipping those producers for that agent.
2. **Request-side filter** — adding an optional `"suppress": ["memory.*"]` field on the request body.

For v0.26 the plugin always receives the full resolved section list and picks what to use; the suppress mechanism is deferred to a later additive contract change driven by concrete adapter needs.
