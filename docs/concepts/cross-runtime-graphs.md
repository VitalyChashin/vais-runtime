# Cross-runtime graph refs

Shipped in v0.20. A graph node can reference an agent deployed on a **different runtime instance** — one that runs in a separate process, pod, or cluster — by setting `ref.runtimeUrl` in the node's manifest. The calling runtime (runtime A) invokes the remote agent over HTTP using the same `/v1/agents/{id}/invoke` endpoint that the CLI uses, then merges the response back into the graph state.

## When to use this vs A2A-as-tool

| Pattern | Choose when |
|---|---|
| **Cross-runtime `runtimeUrl`** | The remote agent is deployed on another vais-agents runtime. You want the graph's normal node lifecycle events (`node.started`, `node.completed`) and you control both runtimes. |
| **Cross-runtime `a2aUrl`** | The remote agent exposes a standard A2A endpoint (may be a non-vais runtime). Same first-class graph-node treatment as `runtimeUrl`, but invoked via `IA2AGraphNodeInvoker` over the A2A protocol. |
| **A2A-as-tool** | You want the remote A2A agent to be a tool call inside a single LLM turn rather than its own graph node. |

## The `runtimeUrl` / `a2aUrl` fields

`GraphAgentRef` is the record that appears on `kind: Agent` graph nodes. It has four optional fields:

```csharp
public sealed record GraphAgentRef(
    string Id,
    string? Version = null,
    string? RuntimeUrl = null,
    string? A2AUrl = null);
```

In YAML manifests:

```yaml
spec:
  nodes:
    - id: enrich
      kind: Agent
      ref:
        id: enricher-agent
        version: "1.0"
        runtimeUrl: "https://runtime-b.internal"
    - id: translate
      kind: Agent
      ref:
        id: translator-agent
        a2aUrl: "https://a2a.partner.example/agents/translator"
```

- **`RuntimeUrl = null` AND `A2AUrl = null`** (default) — the agent is local. The orchestrator resolves it via `IAgentRegistry` on the calling runtime.
- **`RuntimeUrl = "https://…"`** — the agent is on another vais-agents runtime. The orchestrator calls `IAgentRemoteInvoker.InvokeAsync` with the absolute URL.
- **`A2AUrl = "https://…"`** — the agent is a remote A2A endpoint. The orchestrator calls `IA2AGraphNodeInvoker.InvokeAsync` with the absolute URL; the default impl ships in `Vais.Agents.Protocols.A2A` as `A2AGraphNodeInvoker`. Manifest loading rejects non-absolute http/https URIs and rejects manifests that set both `runtimeUrl` and `a2aUrl` on the same ref ("mutually exclusive — specify exactly one remote endpoint").
- **`Version = null` on a remote ref** — the remote runtime resolves the latest registered version of the agent. No extra round-trip; the remote runtime applies the same "latest" semantics it would for any `GET /v1/agents/{id}/invoke` call without an explicit version query parameter.

> **Round-trip gap.** `AgentGraphManifestEnvelope.Serialize` does not currently write the `a2aUrl` field back out — only `id`, `version`, and `runtimeUrl` are emitted. Manifests with `a2aUrl` load and execute correctly, but a load → serialize → load cycle drops the field. Track via the manifest+DI coverage plan; mirrors the same shape as other recurring envelope-vs-loader gaps.

## How the orchestrator routes

`InProcessGraphOrchestrator` and the MAF `GraphNodeExecutor` both apply the same branch before consulting the local registry:

```
ExecuteNodeAsync(node, ...)
  if node.Ref?.RuntimeUrl != null
    → IAgentRemoteInvoker.InvokeAsync(runtimeUrl, handle, request, bearerToken)   // unary
    → IAgentRemoteInvoker.StreamAsync(runtimeUrl, handle, request, bearerToken)    // streaming (v0.33)
    → merge result into state["lastAssistantText"]
  else if node.Ref?.A2AUrl != null
    → IA2AGraphNodeInvoker.InvokeAsync(a2aUrl, handle, request)
    → merge result into state["lastAssistantText"]
  else
    → IAgentRegistry.GetAsync(node.Ref.Id)
    → IAgentLifecycleManager.InvokeAsync(...)
```

The `IAgentRemoteInvoker` and `IA2AGraphNodeInvoker` are both injected as optional dependencies. If a remote node is reached and the matching invoker is not registered, the orchestrator / executor throws `InvalidOperationException` with a hint at the missing DI registration. The runtime host wires `IAgentRemoteInvoker` automatically; wire `IA2AGraphNodeInvoker` by registering `Vais.Agents.Protocols.A2A.A2AGraphNodeInvoker`. In-process test harnesses that don't need remote routing leave both null — local-only graphs keep working.

## Bearer token forwarding

The calling runtime forwards the inbound `Authorization: Bearer …` header verbatim to the remote runtime. The forwarding is transparent and zero-config:

- `AgentGraphLifecycleManager` resolves the token via `Func<string?>? bearerTokenProvider`.
- `Runtime.Host` provides the function from `IHttpContextAccessor.HttpContext?.Request.Headers.Authorization`.
- The token is resolved fresh on each graph run (not cached), so token rotation is safe.

**Security considerations:**

- Both runtimes must trust the same identity provider (or the same API key scheme) for the bearer to be accepted. For service-to-service identity hardening — OAuth 2.0 token exchange, workload identity, or outbound `client_credentials` grants — see [Configure OIDC identity](../guides/configure-oidc-identity.md) (v0.29 `Vais.Agents.Identity.Oidc`).
- If the calling runtime has no inbound HTTP context (e.g. a background job), `bearerTokenProvider` returns null and no `Authorization` header is sent. The remote runtime's policy engine governs whether anonymous invocations are allowed.
- Do not use `runtimeUrl` with untrusted hosts. The invoker sends the caller's bearer token to whatever URL is in the manifest. Validate manifests before applying.

## `RemoteAgentInvocationException`

`HttpAgentRemoteInvoker` throws `RemoteAgentInvocationException` on any non-2xx response. The exception carries three read-only properties:

| Property | Type | Meaning |
|---|---|---|
| `RuntimeUrl` | `string` | The remote runtime URL that was called. |
| `Status` | `HttpStatusCode` | The HTTP status returned by the remote runtime. |
| `IsRetryable` | `bool` | `true` for 503, 504, 429; `false` for everything else (including 404, 400, 401, 403). |

The invoker performs **2 retries** (3 total attempts) with fixed back-off (500 ms / 1 000 ms) on retryable status codes before throwing. Non-retryable statuses throw immediately.

The exception propagates from `ExecuteNodeAsync` and is treated the same as any other node failure: the run phase flips to `Error` and a `graph.failed` event is emitted with structured detail.

## HTTP client pooling

`HttpAgentRemoteInvoker` maintains one `HttpClient` per unique normalised `runtimeUrl` (scheme + host + port + base path, trailing slashes stripped). Clients are created on first use and reused for the lifetime of the invoker singleton. There is no eviction; add a restart if you need to cycle the pool (e.g. after a certificate rotation that requires a new TLS session).

## Streaming cross-remote (v0.33)

`IAgentRemoteInvoker.StreamAsync` ships in v0.33. When a graph node has `runtimeUrl` set and the orchestrator is invoked via a streaming path, `StreamAsync` POSTs to `/v1/agents/{id}/invoke/stream` on the remote runtime, parses the SSE response with `AgentSseParser`, and yields `AgentEvent` frames to the local graph run. Delta events flow through from the remote runtime to the caller — no buffering.

Bearer token and identity-provider forwarding behaviour is identical to `InvokeAsync`. A 501 response from the remote runtime surfaces as `RemoteAgentInvocationException` with `IsRetryable = false`.

## Limitations

- **`a2aUrl` round-trip gap.** `JsonAgentGraphManifestLoader` reads + validates `a2aUrl`, and the orchestrators / executors route on it correctly, but `AgentGraphManifestEnvelope.Serialize` does not emit the field. Manifests fetched via the HTTP API and re-applied locally lose the `a2aUrl` value.
- **No per-remote Polly config.** Retry + circuit-breaker options apply globally, not per `runtimeUrl`. Per-remote configuration is deferred to a future release.

## See also

- [Graph as a first-class deployable](graph-as-deployable.md) — `AgentGraph` manifest format, management surface.
- [Graph orchestration concept](graph-orchestration.md) — node kinds, edge predicates, state bindings.
- [Compose a graph across runtimes](../guides/compose-a-graph-across-runtimes.md) — step-by-step walkthrough.
- [Deploy a graph to the runtime](../guides/deploy-a-graph-to-the-runtime.md) — basic graph deployment walkthrough.
- [Configure OIDC identity](../guides/configure-oidc-identity.md) — service-to-service identity for cross-runtime calls.
