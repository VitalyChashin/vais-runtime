# Extension Handler Protocol

**Version:** 0.30  
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
| 0.30 | 2026-05-19 | Initial extension handler protocol (Phase B) |
