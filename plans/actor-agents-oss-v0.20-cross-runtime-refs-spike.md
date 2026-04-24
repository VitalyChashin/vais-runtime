# Pillar E spike — v0.20 cross-runtime graph refs

**Status:** Locked (findings doc written same session).

## Context

Today `GraphAgentRef(string Id, string? Version)` is purely local. The `InProcessGraphOrchestrator` resolves it against `IAgentRegistry.GetAsync(id, version)` and invokes via `IAgentLifecycleManager.InvokeAsync(handle, request)`. Both the InProcess orchestrator and the MAF-backed `GraphNodeExecutor` follow this pattern.

US-7 — "graph re-uses already-deployed agents on a different runtime" — requires that a graph node referencing an agent on runtime B behaves identically to one referencing an agent on runtime A (structured output bindings, metadata, signals). This rules out wrapping the cross-runtime call as an A2A `ITool`, which is text-in/text-out and loses the structured path.

`IAgentControlPlaneClient` already exists with full-fidelity `InvokeAsync` / `InvokeStreamAsync` over HTTP. The cross-runtime path is thin wrapping, not a new protocol.

---

## Open questions

### Q1 — Discovery vs explicit config

Should the runtime URL for a remote agent be:
- **(A) Explicit-config** — baked into `GraphAgentRef` as `RuntimeUrl?: string`. The graph manifest author knows which runtime holds the agent.
- **(B) A2A discovery** — use A2A agent-card discovery to find the remote agent's endpoint from a well-known registry or DNS.
- **(C) Both** — explicit config as primary; A2A as optional override.

**Lean:** A. Explicit-config. A2A-as-tool already exists for loose-coupling use cases; A2A discovery adds a new protocol hop at the resolution layer. Explicit URL is operator-friendly, debuggable, and sufficient for the v0.20 preview. A2A field on `GraphAgentRef` can be additive in v0.21.

---

### Q2 — Where does `RuntimeUrl` live?

- **(A)** In `GraphAgentRef` directly: `record GraphAgentRef(string Id, string? Version, string? RuntimeUrl)`.
- **(B)** In a new `RemoteRuntimeRef` wrapper referenced by `GraphNode`.
- **(C)** In `GraphNode` itself (`RuntimeUrl?: string` alongside `Ref`).

**Lean:** A. `GraphAgentRef` is the natural place — it's the identity of the remote agent. Adding it there keeps `GraphNode` unchanged. The field is additive (null = local, as today).

---

### Q3 — Transport for remote invocation

- **(A)** Reuse `IAgentControlPlaneClient` — it has `InvokeAsync(AgentHandle, InvocationRequest, ct)`. One HTTP client instance per unique `RuntimeUrl`.
- **(B)** New dedicated `IAgentRemoteInvoker` interface backed by a raw `HttpClient`.
- **(C)** Wrap A2A HTTP client to avoid new code.

**Lean:** A + thin interface shim. Define `IAgentRemoteInvoker` with just `InvokeAsync` + `StreamAsync` so the orchestrator doesn't depend on the full `IAgentControlPlaneClient` surface. `HttpAgentRemoteInvoker` implements it by delegating to `IAgentControlPlaneClient`. This keeps the orchestrator testable (stub the invoker) without exposing the full client.

---

### Q4 — Orchestrator changes

Where to branch on remote vs local?

- **(A)** `InProcessGraphOrchestrator.ExecuteNodeAsync` checks `node.Ref?.RuntimeUrl != null` → routes to injected `IAgentRemoteInvoker`; null → existing local path.
- **(B)** New `IAgentNodeInvoker` abstraction that unifies local + remote, injected into the orchestrator.
- **(C)** Subclass or decorator of `InProcessGraphOrchestrator`.

**Lean:** A. Single branch in `ExecuteNodeAsync`. Local path unchanged; remote path is 5-10 lines. MAF `GraphNodeExecutor` gets the same branch. No new abstraction needed for v0.20.

---

### Q5 — Version null on remote

If `GraphAgentRef.Version` is null and `RuntimeUrl` is set:
- **(A)** Round-trip query to remote: `GET {runtimeUrl}/v1/agents/{id}` to resolve latest version, then invoke.
- **(B)** Fail-fast: `RuntimeUrl` requires explicit `Version` — null version on a remote ref is a validation error.
- **(C)** Pass null through and let the remote runtime resolve.

**Lean:** C. The remote runtime already handles null version → latest (same semantics as local). Avoids an extra round-trip. Document it in the manifest format spec.

---

### Q6 — Identity propagation

When runtime A invokes an agent on runtime B:
- **(A) Bearer forwarding** — pass the inbound bearer token from the graph caller's request context through to the outbound call.
- **(B) Service-account token** — operator pod uses its own projected SA token (pod-to-pod, K8s-native).
- **(C) OIDC token exchange** — derive a short-lived token for B's audience from A's identity.
- **(D) Configurable** — `RemoteRuntimeOptions` lets operators choose.

**Lean:** A for v0.20 (bearer forwarding), D as the pattern for future. Bearer forwarding is zero-config and correct for same-org deployments where both runtimes trust the same IdP. OIDC exchange is the right answer for production multi-org but adds a new dependency (token-exchange endpoint); defer to v0.21 security hardening pillar.

---

### Q7 — Failure modes

If a remote agent returns 404, 503, or times out:
- **(A)** Map to existing exception types: 404 → `AgentNotFoundException`; 503/timeout → `AgentInvocationException`. Orchestrator treats them the same as local failures (phase → Error, conditions flipped).
- **(B)** New `RemoteAgentInvocationException` that carries the upstream HTTP status so callers can distinguish local vs remote failures.
- **(C)** Retry on 503 within the orchestrator (with backoff); 404 is non-retryable.

**Lean:** B + C. A new exception type is small cost and lets the runtime surface useful diagnostics to `graph.failed` events. Retry on transient errors (503, timeout) is table-stakes for network calls; use the same `HttpPolicyOptions` that `IAgentControlPlaneClient` already uses (Polly retry). 404 → immediate non-retryable failure.

---

### Q8 — MAF parity

The MAF-backed `GraphNodeExecutor` has the same `IAgentRegistry.GetAsync` + `IAgentLifecycleManager.InvokeAsync` resolution pattern. Should both orchestrators get cross-runtime support in v0.20?

- **(A) Both.** Consistent behavior; `GraphNodeExecutor` is already a thin adapter.
- **(B) InProcess only.** MAF graph support is narrower (fewer deployments); defer to v0.21.

**Lean:** A. The branch in `GraphNodeExecutor` mirrors `InProcessGraphOrchestrator` exactly and is ~5 lines. Parity prevents a footgun where switching orchestrators changes cross-runtime behavior.

---

### Q9 — Manifest loader changes

`GraphAgentRef` grows `RuntimeUrl`. The JSON + YAML loaders must:
- Parse `ref.runtimeUrl` from the manifest document.
- Validate format (must be a valid absolute URI if present, scheme `http` or `https`).
- Apply same validation to the K8s CRD spec projector.

**Lean:** Additive change. Add `runtimeUrl` as an optional string in `ParseNodes` in `JsonAgentGraphManifestLoader`. URI validation is a one-liner with `Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"`. CRD projector adds `RuntimeUrl = spec.RuntimeUrl`.

---

### Q10 — Remote agent ref in A2A invoker (additive, v0.21)

Should `GraphAgentRef` also gain `A2AUrl?: string` in v0.20, or defer?

- **(A)** Add now — additive, costs nothing if null. Users who want A2A semantics get a path even if it's text-in/text-out with structural divergence documented.
- **(B)** Defer — add in v0.21 when A2A structured output (`output_schema`) lands in the A2A spec. Before then, A2A-as-tool is the workaround.

**Lean:** B. Adding a field that can't be wired to a structurally-equivalent invoker today is misleading. `A2AUrl` belongs in v0.21 alongside the A2A structured output pillar.

---

## Summary of leans

| Q | Decision |
|---|---|
| Q1 Discovery | Explicit-config (`RuntimeUrl` in `GraphAgentRef`) |
| Q2 Placement | `GraphAgentRef` directly (additive field) |
| Q3 Transport | `IAgentRemoteInvoker` shim over `IAgentControlPlaneClient` |
| Q4 Orchestrator | One branch in `ExecuteNodeAsync` on `Ref.RuntimeUrl != null` |
| Q5 Null version | Pass through to remote runtime |
| Q6 Identity | Bearer forwarding (v0.20), configurable (v0.21) |
| Q7 Failures | `RemoteAgentInvocationException` + Polly retry on transient |
| Q8 MAF parity | Both orchestrators in v0.20 |
| Q9 Loader | Additive `runtimeUrl` field, URI validation |
| Q10 A2A | Defer to v0.21 |
