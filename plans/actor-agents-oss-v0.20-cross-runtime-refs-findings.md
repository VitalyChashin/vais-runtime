# v0.20.0-preview — Cross-runtime graph refs (Pillar E) — findings

Decisions-locked follow-up to [the spike](./actor-agents-oss-v0.20-cross-runtime-refs-spike.md). Captures the 10 blocking-question answers plus the additive API surface the [pillar plan](./actor-agents-oss-v0.20-cross-runtime-refs-pillar.md) consumes.

Written 2026-04-21. Spike direction confirmed; answers below are the agreed shape.

---

## Decision log (Q1–Q10)

| # | Question | Decision | Rationale |
|---|---|---|---|
| Q1 | Discovery vs explicit config | Explicit-config — `RuntimeUrl?: string` on `GraphAgentRef`. | A2A discovery adds a new protocol hop at the resolution layer. Explicit URL is operator-friendly, debuggable, sufficient for v0.20. A2A field additive in v0.21. |
| Q2 | Where does `RuntimeUrl` live? | In `GraphAgentRef` directly: `record GraphAgentRef(string Id, string? Version, string? RuntimeUrl)`. | `GraphAgentRef` is the identity of the remote agent. `GraphNode` unchanged. Null = local (as today). |
| Q3 | Transport | Define `IAgentRemoteInvoker` with `InvokeAsync` + `StreamAsync`. `HttpAgentRemoteInvoker` delegates to `IAgentControlPlaneClient`. One client per unique `RuntimeUrl`. | Keeps orchestrator testable (stub the invoker) without depending on the full `IAgentControlPlaneClient` surface. |
| Q4 | Orchestrator changes | One branch in `InProcessGraphOrchestrator.ExecuteNodeAsync` on `node.Ref?.RuntimeUrl != null`. Local path unchanged. Same in MAF `GraphNodeExecutor`. | Minimal blast radius. New branch is 5–10 lines per orchestrator. No new abstraction in v0.20. |
| Q5 | Null version on remote | Pass null through to remote runtime (remote resolves "latest"). | Remote runtime already has null-version-→-latest semantics. Avoids extra round-trip. Documented in manifest format spec. |
| Q6 | Identity propagation | Bearer forwarding (v0.20): pass inbound `Authorization: Bearer` header to outbound call. Configurable (`RemoteRuntimeOptions`) deferred to v0.21. | Zero-config for same-org deployments where both runtimes share an IdP. OIDC token exchange adds a dependency; defer to security hardening pillar. |
| Q7 | Failure modes | `RemoteAgentInvocationException(string RuntimeUrl, HttpStatusCode Status, string? Detail)` carrying upstream HTTP status. 404 → non-retryable (wrapped). 503 / timeout → Polly retry using same `HttpPolicyOptions` as `IAgentControlPlaneClient`. | Small cost; useful diagnostics for `graph.failed` events. Retry is table-stakes for network calls. |
| Q8 | MAF parity | Both `InProcessGraphOrchestrator` and MAF `GraphNodeExecutor` updated in v0.20. | The branch mirrors exactly. Switching orchestrators must not change cross-runtime behaviour. |
| Q9 | Manifest loader changes | Additive `runtimeUrl` optional field in `JsonAgentGraphManifestLoader.ParseNodes`. URI validation: `Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"`. YAML loader delegates to JSON loader (no separate work). K8s CRD projector adds `RuntimeUrl = spec.RuntimeUrl`. | Purely additive. One-liner validation. Existing manifests round-trip unchanged. |
| Q10 | A2A field on `GraphAgentRef` | Defer to v0.21 (alongside A2A structured output pillar). | Adding a field that cannot be wired to a structurally-equivalent invoker today is misleading. A2A-as-tool is the v0.20 workaround; document it. |

---

## Wire contract — locked shapes

```csharp
// Vais.Agents.Core  (additive — existing record grows one field)

// BEFORE
public sealed record GraphAgentRef(string Id, string? Version = null);

// AFTER
public sealed record GraphAgentRef(string Id, string? Version = null, string? RuntimeUrl = null);
```

```csharp
// Vais.Agents.Control.Abstractions  (new)

/// <summary>Thin invoker for a single remote runtime endpoint.</summary>
public interface IAgentRemoteInvoker
{
    ValueTask<InvocationResult> InvokeAsync(
        string runtimeUrl,
        AgentHandle handle,
        InvocationRequest request,
        string? bearerToken,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<InvocationStreamChunk> StreamAsync(
        string runtimeUrl,
        AgentHandle handle,
        InvocationRequest request,
        string? bearerToken,
        CancellationToken cancellationToken = default);
}
```

```csharp
// Vais.Agents.Control.Http.Client  (new implementation)

internal sealed class HttpAgentRemoteInvoker : IAgentRemoteInvoker
{
    // Keyed HttpClient pool keyed by runtimeUrl (IHttpClientFactory or manual ConcurrentDictionary)
    // Delegates to IAgentControlPlaneClient per URL.
    // Injects Authorization: Bearer <bearerToken> on every outbound call.
}
```

```csharp
// Vais.Agents.Core  (new exception)

public sealed class RemoteAgentInvocationException : AgentInvocationException
{
    public string RuntimeUrl { get; }
    public HttpStatusCode Status { get; }

    public RemoteAgentInvocationException(
        string runtimeUrl,
        HttpStatusCode status,
        string? detail = null,
        Exception? inner = null);

    public bool IsRetryable => Status is HttpStatusCode.ServiceUnavailable
                                      or HttpStatusCode.GatewayTimeout
                                      or HttpStatusCode.TooManyRequests;
}
```

```csharp
// Vais.Agents.Core  (orchestrator branch — pseudocode)

// InProcessGraphOrchestrator.ExecuteNodeAsync — remote branch (new, ~10 lines):
if (node.Ref?.RuntimeUrl is string runtimeUrl)
{
    var bearerToken = _httpContextAccessor?.HttpContext?.Request.Headers
                          .Authorization.ToString()
                          .Replace("Bearer ", "", StringComparison.Ordinal);
    var handle = new AgentHandle(node.Ref.Id, node.Ref.Version ?? string.Empty);
    return await _remoteInvoker.InvokeAsync(runtimeUrl, handle, request, bearerToken, ct);
}
// else: existing local IAgentRegistry.GetAsync + IAgentLifecycleManager.InvokeAsync path
```

---

## Loader diff — `ParseNodes` extension

```csharp
// JsonAgentGraphManifestLoader.ParseNodes (additive)
var runtimeUrl = nodeObj.TryGetProperty("runtimeUrl", out var ruProp)
    ? ruProp.GetString()
    : null;
if (runtimeUrl is not null
    && (!Uri.TryCreate(runtimeUrl, UriKind.Absolute, out var uri)
        || uri.Scheme is not ("http" or "https")))
{
    throw new AgentManifestValidationException($"node '{id}': runtimeUrl must be an absolute http/https URI");
}
var @ref = refObj.TryGetProperty("id", out var refId) ? new GraphAgentRef(
    Id:         refId.GetString()!,
    Version:    refObj.TryGetProperty("version", out var v) ? v.GetString() : null,
    RuntimeUrl: runtimeUrl
) : null;
```

---

## K8s CRD spec projector diff

```csharp
// AgentGraphCrdSpecProjector.Project (additive)
RuntimeUrl = specNode.TryGetProperty("runtimeUrl", out var ru) ? ru.GetString() : null
```

The CRD schema at `deploy/crds/vais.io_agentgraphs.yaml` gains `runtimeUrl: { type: string }` under `spec.nodes.items.properties.ref`.

---

## Manifest YAML example (cross-runtime node)

```yaml
apiVersion: vais.agents/v1
kind: AgentGraph
metadata:
  name: cross-runtime-pipeline
  id: cross-pipeline
  version: "1.0"
spec:
  entry: local-step
  nodes:
    - id: local-step
      kind: Agent
      ref:
        id: classifier
        version: "1.0"
    - id: remote-step
      kind: Agent
      ref:
        id: enricher
        version: "2.0"
        runtimeUrl: https://runtime-b.svc.cluster.local
    - id: done
      kind: End
  edges:
    - from: local-step
      to: remote-step
    - from: remote-step
      to: done
```

---

## What does NOT change

- `GraphNode` record shape — `RuntimeUrl` lives on `GraphAgentRef`, not `GraphNode`.
- `AgentGraphManifest` shape — no new top-level fields.
- Existing `InvocationRequest` / `InvocationResult` wire shapes — unchanged.
- A2A tooling — no change; A2A-as-tool is the existing workaround for text-in/text-out cross-agent delegation.
- Bearer token format — v0.20 forwards the inbound `Authorization` header verbatim; no token exchange.

---

## Deferred to v0.21

- `A2AUrl` on `GraphAgentRef` (Q10) — add with A2A structured output pillar.
- Configurable identity propagation (`RemoteRuntimeOptions`, OIDC token exchange) — add in security hardening pillar.
- Per-`RuntimeUrl` timeout + Polly options — `HttpPolicyOptions` reused as-is in v0.20; per-remote tuning deferred.
- `vais get-remote-runtimes` / runtime topology discovery — Pillar F scope or later.
