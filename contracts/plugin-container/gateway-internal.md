# Internal Gateway Protocol

**Version:** 0.27  
**Status:** Frozen — breaking-change boundary. Changes require a version bump and coordinated update to the SDK (IP-2) and runtime shim (IP-3).

---

## Overview

Endpoints the runtime exposes for container → runtime callbacks. Called by plugin containers via the gateway clients provided in `InvokeRequest` (`llmGatewayUrl`, `toolGatewayUrl`).

**Network binding:** Internal port (default 5001), separate from the Kubernetes Service's external-facing port. Network policy restricts access to plugin container CIDRs only.

**Path prefix:** All gateway endpoints mount under `/v1/container-gateway/*` on the internal port. Older drafts of this contract referenced unprefixed paths (`/v1/llm/complete` etc.); those never shipped — the prefix has been in place since IP-3.

**Authentication:** Every request must carry `Authorization: Bearer <callToken>`, where `callToken` is the value from `InvokeRequest.context.callToken`. Token generation algorithm and key rotation are specified in IP-3. Token validity window: `invokeTimeoutSeconds + 30s` to allow for clock skew (separate from the OTLP-receiver token, which uses a 24 h TTL — see [plugin-protocol §OTLP receiver]).

**Required headers on every gateway request:**

```
Authorization: Bearer <callToken>
X-Agent-Id: <agentId>
X-Run-Id: <runId>
```

**Recommended:**

```
traceparent: <traceparent from InvokeRequest.context>
```

`traceparent` is W3C-standard for trace propagation and the runtime forwards it onto the outbound LLM / tool call when present. It is **not** validated by the endpoint filter today, so plugins that omit it still succeed — the only consequence is a broken trace tree in Langfuse / Tempo. SDKs should always emit it.

---

## `POST /v1/container-gateway/llm/complete`

LLM completion callback. Routes through the full `ILlmGateway` middleware chain — token accounting, Langfuse tracing, and rate limiting apply exactly as for direct agent invocations.

### Request

The body is a **discriminated union**: exactly one of `messages` or `sections` must be present. Both populated, or both null/empty, → HTTP 400 with `urn:vais-agents:llm-complete-input-conflict`.

**Messages variant** — the original path. Plugin sends pre-flattened conversation history.

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

**Sections variant** (v0.27) — plugin sends typed `Section[]` (typically obtained from `POST /v1/container-gateway/sections/build` and optionally mutated client-side). Runtime runs the canonical `CompletionRequestFlattener` server-side, so the `ILlmGateway` sees a `CompletionRequest` byte-equal to what a runtime-hosted agent would have produced.

```json
{
  "sections": [ "<GatewaySection>", "..." ],
  "modelId": "gpt-4o-mini",
  "options": {
    "temperature": 0.7,
    "maxTokens": 4096
  }
}
```

`<GatewaySection>` is the exact shape `POST /v1/container-gateway/sections/build` returns — see that endpoint for the schema. Plugins typically round-trip the response array verbatim; mutate (drop, reorder, edit payload text) when they want to override runtime composition decisions.

Field rules:
- `messages` / `sections` — mutually exclusive; one required. See discriminator note above.
- `modelId` — optional. When omitted, the gateway falls back to a hardcoded default (`gpt-4o-mini` as of v0.27). Per-agent model defaults are tracked as a follow-up; until they land, plugins that need a specific model should always supply `modelId`.
- `options` — optional; null means "use defaults." Full parameter set is specified in the `ILlmGateway` contract, not here.

### Response (non-streaming)

Same shape regardless of input variant:

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

`Accept: text/event-stream` → SSE with `delta` / `done` events. The wire shape is VAIS-native (not OpenAI-chunk):

```
event: delta
data: {"textDelta": "...", "modelId": "..."}

event: delta
data: {"textDelta": "..."}

event: done
data: {"usage": {"inputTokens": 1200, "outputTokens": 450}}
```

Streaming works on both the `messages` and `sections` variants. The container SDK client pipes these deltas through its own `/v1/stream` SSE response to the caller.

Plugins that want the OpenAI-chunk wire shape (for an OpenAI-compatible SDK on the client side) should use `POST /v1/container-gateway/chat/completions` instead — that endpoint is purely OpenAI-shaped and accepts `messages` only (no `sections` variant; OpenAI clients don't know about VAIS sections).

### Gateway behaviour

- `callToken` is validated; `X-Agent-Id` and `X-Run-Id` are used to reconstruct `AgentContext` for `GatewayEventMiddleware`.
- **Messages variant:** the runtime treats the body as the final `CompletionRequest.History`; no resolver / packer / telemetry runs.
- **Sections variant** (v0.27): the runtime runs the same pipeline a runtime-hosted agent does — `ISectionResolver.ResolveAsync` (re-validate ids + ordering) → `ISectionWindowPacker.PackAsync` (apply the agent's `SectionBudget`) → `SectionTelemetryEmitter.EmitAsync` (fire all configured `ISectionTelemetrySink`s: OTel tags, Prometheus metrics, Langfuse enrichment, `RequestSectionsBuilt` event, structured logs) → `CompletionRequestFlattener.Flatten` → `ILlmGateway` middleware chain → `ICompletionProvider.CompleteAsync`. This restores **telemetry symmetry**: a plugin agent that opts into sections gets the same per-section observability surface a declarative agent does, without having to reimplement flatten in the plugin.
- A completion call from inside the container produces a `vais_gateway_events` row with the correct `agent_id` and `run_id` regardless of input variant.
- `callToken` security algorithm and validation are specified in IP-3.

### When to use which variant

| Use | Choose |
|---|---|
| Plugin already has the assembled `messages` array (legacy default; OpenAI-compatible plugins; plugins integrating non-VAIS frameworks) | `messages` |
| Plugin wants per-section attribution, runtime-side packer enforcement, and per-section telemetry symmetry with declarative agents | `sections` |
| Plugin wants to integrate with an OpenAI-compatible SDK / IDE (continues to work with that SDK's expected wire shape) | use `/v1/container-gateway/chat/completions` (unchanged, OpenAI-shaped) |

The `messages` variant is the default for backwards compatibility — existing plugins (incl. those using `vais_agent_sdk.adapters.openai` to flatten client-side, or external OpenAI-compatible toolchains) keep working unchanged. The `sections` variant is purely additive opt-in.

---

## `POST /v1/container-gateway/chat/completions`

OpenAI-compatible chat completions variant of `/llm/complete`. Accepts the OpenAI request envelope (`messages`, `model`, `temperature`, `tools`, etc.) so that plugins built on the OpenAI Python / JS SDK or any OpenAI-compatible toolchain (LiteLLM, OpenRouter, Continue, Cline, …) can target the runtime without a custom client. Routes through the same `ILlmGateway` middleware chain as `/llm/complete`; the only difference is the wire shape on the request side and the chunk shape on the streaming response side (OpenAI `data: {...}` chunks rather than the VAIS-native `event: delta` / `event: done`).

Accepts `messages` only — no `sections` variant. OpenAI clients don't know about VAIS sections, and a plugin that wants section-pipeline semantics should use `/llm/complete` instead.

Auth + headers are identical to `/llm/complete` (`Authorization: Bearer <callToken>` + `X-Agent-Id` + `X-Run-Id`).

---

## `GET /v1/container-gateway/tools/list`

Enumerate the agent's tool registry as seen by the runtime. Returns the post-filter, post-merge view that the LLM would see if the runtime were calling it directly. Auth + headers identical to the POST endpoints.

### Response

```json
{
  "tools": [
    {
      "name": "web_search",
      "description": "Search the public web for relevant pages.",
      "parametersSchema": { "type": "object", "properties": { "query": { "type": "string" } }, "required": ["query"] }
    },
    ...
  ]
}
```

Useful for plugins whose LLM prompt format requires the tool catalogue inline (some LangGraph node patterns, OpenAI Assistants migration scenarios). For plugins relying on the section pipeline, `/sections/build` already returns a `ToolDeclaration` section with this payload.

---

## `POST /v1/container-gateway/tools/invoke`

Tool invocation callback. Routes through the `IToolGateway` middleware chain. Same `callToken` validation as `/llm/complete`.

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

## `POST /v1/container-gateway/sections/build`

Section pipeline callback (v0.26). Reuses the named agent's runtime-side section composition (history reducer → composer → `IContextProvider` chain → resolver) and returns the typed `Section[]` the plugin would otherwise have to derive from `InvokeRequest.messages`. The packer is **not** run — the plugin decides which sections to include in its own LLM call. Same `callToken` validation and required-headers regime as `/llm/complete`.

This endpoint is purely additive — default plugin behaviour (consume `InvokeRequest.messages`) is unchanged. Plugins opt in by calling `/v1/container-gateway/sections/build` and then either flattening with a shipped adapter (`vais_plugin.adapters.openai`) or mapping sections into a framework-native layout (LangGraph state slots, LangChain `ChatPromptTemplate` parts, etc.).

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
  | `ToolDeclaration` | `{ "tools": [ { "name": "...", "description": "...", "parametersSchema": { ... } }, ... ] }`. **Read-only from the plugin's perspective.** The runtime emits these as part of the resolved section list, but sending a `ToolDeclaration` section back in the `sections` array on `/llm/complete` is rejected with `400` — tool catalogues are registry-bound, and round-tripping a mutated copy would silently lose the registry's view. Plugins that need a different tool surface should drop the section before relaying. |
  | `ResponseFormat` | `{ "spec": { "schema": { ... }, "name": "...", "strict": true } }` |
  | `Metadata` | `{ "values": { "<key>": <value>, ... } }` — never flattens to the wire; observability only |
- `sections[].order` — optional integer. Explicit-order sections sort first within their kind (ascending); null-order sections cluster at the end in registration order.
- `sections[].producerId` — optional. Conventionally the producer's type name (e.g. `PersonaContributor`, `KnowledgeRetrievalContextProvider`). Required for per-section observability attribution.
- `sections[].budget` — optional `{ "priority": int, "maxChars": int? }`. Priority 0 = critical, 10 = drop first; default is 5 when omitted. Plugins that build their own request typically ignore `budget` — it's informative, not enforced on this path (the packer doesn't run).
- `totalChars` — sum of character lengths across all sections that carry text (`SystemSegment`, turn-shaped, `ToolDeclaration` summaries, `ResponseFormat` schema raw text). `Metadata` sections contribute 0. The plugin uses this for budget-aware adapter rendering.

### Gateway behaviour

- `callToken`, `X-Agent-Id`, `X-Run-Id` validated and used to reconstruct `AgentContext` for the per-turn `IContextProvider` invocation — providers see the same context they would in a runtime-hosted agent.
- The `messages` payload is converted to a candidate `CompletionRequest` (the same shape the runtime's `StatefulAiAgent` would assemble). Providers receive this candidate via `ContextInvocationContext.Candidate` — so retrieval producers can read the last user turn for their query, history-shaped sections reflect the plugin's view, etc.
- The pipeline runs `composer.ComposeSectionsAsync → IContextProvider.InvokeAsync (per registered provider) → ISectionResolver.ResolveAsync`. Packer + telemetry emitter are **not** run; section telemetry sinks (`vais_request_section_*` metrics, `RequestSectionsBuilt` events) for plugin-served sections come from the plugin's subsequent `/v1/container-gateway/llm/complete` call, not this endpoint. The history reducer is also skipped — the plugin already owns history shape.
- Producer exceptions propagate as HTTP 500 with a JSON `{ "error": "...", "producerId": "..." }` body. Plugins should treat this as a recoverable error and fall back to `InvokeRequest.messages`.

### Plugin-shaped sections (deferred)

Some plugins build their own context (LangGraph state, persistent agent threads, framework-internal memory) and want to suppress runtime-emitted sections. Two future directions:

1. **Manifest-declared override** — `spec.sections.suppress: [memory.*, history.window]` on the agent manifest, with the runtime skipping those producers for that agent.
2. **Request-side filter** — adding an optional `"suppress": ["memory.*"]` field on the request body.

For v0.26 the plugin always receives the full resolved section list and picks what to use; the suppress mechanism is deferred to a later additive contract change driven by concrete adapter needs.
