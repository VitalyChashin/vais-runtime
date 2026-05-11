# ADR 0005: Container plugin protocol design

- **Status:** Accepted — 2026-05-08 (IP-1)
- **Context bounded by:** Phase 1 of the Vais.Agents library — container plugin model.
- **Supersedes:** the subprocess plugin model's `restore_state` / `get_state` / `userMessage` surface, which was never a public contract — the container model replaces it entirely.

## Context

The container plugin model moves agent logic from an in-process Python subprocess into an OCI container. Three design choices in this model depart from the earlier subprocess approach in ways that reviewers are likely to question. Each is addressed below.

## Decision

### 1. Per-call `opaqueState` round-trip instead of `restore_state` / `get_state`

The subprocess model required `restore_state` on startup because the subprocess held conversation state in memory between calls — the grain needed to re-hydrate the subprocess after a pod restart. The container model makes no inter-call state assumption. Container replicas are stateless; the grain round-trips the complete plugin-private context (history, LangGraph checkpoint, any other blob) on every call as `opaqueState`.

Consequences:
- Any replica can serve any request. Session affinity is not required.
- The standalone topology (multiple replicas, independent scaling) is viable without a sticky-session load balancer.
- The startup restore step is eliminated. Startup is `GET /health` → ready; no state reload.
- The `opaqueState` field is a raw JSON object in the request body (not base64) — the entire request is already JSON, and base64 adds encoding overhead with no security benefit at this layer.

`OpaqueStateDeserializationError` (HTTP 422) is the recovery path when the grain-stored blob is incompatible with a newly deployed container version. On 422, the grain clears stored state and retries with `opaqueState: null` (fresh-start).

### 2. `messages: List[Message]` instead of `userMessage: string`

The preprocessing pipeline (`IAgentPreprocessor` chain) assembles the full enriched context — history, injected memories, system prompt — before calling the container. The container passes `messages` directly to its LLM client; no history assembly is required inside the plugin.

A bare `userMessage: string` would require the container to re-implement history management, which the platform already handles and which caused the `OpaqueState` size problem in the subprocess model (history was duplicated between the platform and the agent's internal state).

The `Message` type is a strict subset of `Microsoft.Extensions.AI`'s `ChatMessage` shape. Python and C# SDKs pass it directly to their LLM clients without transformation. The C# runtime maps `ChatTurn` ↔ `Message` at the container boundary, keeping Abstractions free of an MEAI dependency.

### 3. Gateway callback over direct DI access

DI injection is only possible within the same process. The container model is intentionally cross-process, so DI is not available to the container. The callback approach enforces architectural principle P4 (gateway is the only LLM/tool path) at the protocol level:

- The only LLM client the plugin SDK exposes is pre-configured to the `llmGatewayUrl` from the request.
- The only tool client is pre-configured to `toolGatewayUrl`.
- A plugin author cannot accidentally bypass the gateway — there is no other LLM client API in the SDK.

`callToken` is per-call and short-lived (`timeoutSeconds + 30s`). The runtime validates it on every callback, ensuring a container can only make LLM/tool calls within the budget of the invocation that issued the token. The token generation algorithm and key-rotation scheme are specified in IP-3.

## Why not …

| Option | Why rejected |
|---|---|
| Persist state in the container; use `restore_state` on restart | Replicas are ephemeral; in-container state means session affinity is required and scaling adds replicas that can't serve existing sessions. Grain durability is the anchor. |
| Pass `userMessage: string` only | Container must re-implement history assembly. History duplicated between grain and agent state — the root cause of the subprocess model's `OpaqueState` size issue. |
| Allow direct LLM client calls from the container | Bypasses P4 (gateway as only LLM path), disables token accounting, Langfuse tracing, and rate limiting for container-originated calls. Undetectable at review time. |
| Base64-encode `opaqueState` | No security benefit at this layer; adds encoding overhead and reduces readability of logged payloads. The request is already JSON. |

## Consequences

- **Positive:** Any replica can serve any session. Horizontal scaling without session affinity.
- **Positive:** Startup sequence is reduced to a health-check poll. No state-restore round-trip.
- **Positive:** All LLM and tool calls from inside the container appear in the same gateway event stream as direct invocations, including token accounting and Langfuse traces.
- **Positive:** `IAgentPreprocessor` is immutable-pipeline style (receives list, returns list). More testable than a mutating context object; mirrors `IToolGatewayMiddleware`.
- **Negative:** `opaqueState` round-trips on every call, even when unchanged. For large checkpoints (LangGraph with many nodes) this adds payload size. Mitigated: the grain stores a diff of `opaqueState` iff the container returns non-null; a future IP can add a content-hash check to skip the store write on unchanged blobs.
- **Negative:** `callToken` is per-invocation, not per-session. A container that fans out multiple parallel LLM calls on the same invocation reuses the same token across all — correct, but requires IP-3's token scheme to be stateless (HMAC over `(runId, agentId, exp)` rather than a server-side lookup).

## Follow-ups

- IP-2: Python and C# SDK implementations of the gateway clients and request/response shapes.
- IP-3: `ContainerAgentShim`, `ContainerPluginHostService`, `callToken` generation algorithm.
- IP-7: Memory plugin as an `IAgentPreprocessor` (Order 20).
