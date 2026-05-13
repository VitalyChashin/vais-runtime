# Control plane

Managed cloud runtimes (Bedrock AgentCore, Temporal, Restate, Dapr, OpenAI Assistants) converge on a universal verb set for agent lifecycles: **Create / Invoke / Signal / Query / Cancel / Update / Evict**. Vais.Agents ships those verbs as `IAgentLifecycleManager` + a value-typed `AgentManifest` that describes an agent deployment. The library tier ships the contracts + an in-memory reference implementation; the runtime tier implements the verbs with an Orleans-backed lifecycle manager, HTTP control plane, Kubernetes operator, OPA policy engine, and Keycloak-backed identity provider.

## Why contract-first

Shipping the contracts before any implementation locks the *shape* in place: consumers can write tooling (CLI, dashboards, migration scripts) against stable records today and swap the runtime underneath. It also means the eventual cloud runtime can't accidentally invent a different surface; it inherits these.

## Core types

```csharp
namespace Vais.Agents;

public sealed record AgentManifest(
    string Name,
    string Version,
    AgentHandlerRef Handler,
    IReadOnlyList<ProtocolBinding>? Bindings = null,
    IReadOnlyList<ToolRef>? Tools = null,
    MemoryRef? Memory = null,
    IdentityRef? Identity = null,
    AutoscalingSpec? Autoscaling = null,
    IReadOnlyDictionary<string, string>? Labels = null);

public sealed record AgentHandlerRef(string Type, string Assembly);
public sealed record ProtocolBinding(string Protocol, IReadOnlyDictionary<string, string>? Config = null);
public sealed record ToolRef(string Name, string Source);
public sealed record MemoryRef(string Backend, IReadOnlyDictionary<string, string>? Config = null);
public sealed record IdentityRef(string Provider, string? Audience = null);
public sealed record AutoscalingSpec(int MinReplicas = 0, int? MaxReplicas = null, int? TargetConcurrencyPerReplica = null);

public sealed record AgentHandle(string AgentId, string? Version = null);
public sealed record AgentInvocationRequest(AgentHandle Target, string Input, IReadOnlyDictionary<string, string>? Headers = null);
public sealed record AgentInvocationResult(string Output, AgentStatus Status, string? Error = null);
public sealed record AgentSignal(AgentHandle Target, string Kind, string? Payload = null);

public sealed record AgentPrincipal(string Subject, IReadOnlyList<string>? Roles = null, IReadOnlyDictionary<string, string>? Claims = null);
public sealed record OutboundCredential(string Scheme, string Token, DateTimeOffset? ExpiresAt = null);

public enum AgentStatus { Unknown, Pending, Active, Failed, Terminated, Evicted }

public interface IAgentRegistry
{
    Task RegisterAsync(AgentManifest manifest, CancellationToken cancellationToken = default);
    Task<AgentManifest?> GetAsync(string name, string? version = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentManifest>> ListAsync(string? labelPrefix = null, CancellationToken cancellationToken = default);
    Task RemoveAsync(string name, string version, CancellationToken cancellationToken = default);
}

public interface IAgentLifecycleManager
{
    Task<AgentHandle> CreateAsync(AgentManifest manifest, CancellationToken cancellationToken = default);
    Task<AgentInvocationResult> InvokeAsync(AgentInvocationRequest request, CancellationToken cancellationToken = default);
    Task SignalAsync(AgentSignal signal, CancellationToken cancellationToken = default);
    Task<AgentStatus> QueryAsync(AgentHandle target, CancellationToken cancellationToken = default);
    Task CancelAsync(AgentHandle target, CancellationToken cancellationToken = default);
    Task UpdateAsync(AgentManifest manifest, CancellationToken cancellationToken = default);
    Task EvictAsync(AgentHandle target, CancellationToken cancellationToken = default);
}

public interface IAgentIdentityProvider
{
    Task<AgentPrincipal?> GetPrincipalAsync(AgentHandle target, CancellationToken cancellationToken = default);
    Task<OutboundCredential> AcquireCredentialAsync(AgentHandle target, string audience, CancellationToken cancellationToken = default);
}
```

## What ships in v0.4

Just `InMemoryAgentRegistry` in Core — a concurrent-dictionary-backed `IAgentRegistry` impl with `Register` / `Remove` helpers, label-prefix filter on `ListAsync`, and "null version → latest lexicographically" semantics on `GetAsync`. Useful for dev, tests, and as a reference implementation for the cloud runtime.

No `IAgentLifecycleManager` impl. No `IAgentIdentityProvider` impl. Those are runtime-tier deliverables — see [`runtime-configuration`](../reference/runtime-configuration.md).

## Using the registry

```csharp
using Vais.Agents.Core;

var registry = new InMemoryAgentRegistry();

var manifest = new AgentManifest(
    Name: "support-agent",
    Version: "1.0.0",
    Handler: new AgentHandlerRef(Type: "MyApp.SupportAgent", Assembly: "MyApp"),
    Tools: new[]
    {
        new ToolRef(Name: "lookup_order", Source: "static"),
        new ToolRef(Name: "send_email", Source: "mcp:email-server"),
    },
    Memory: new MemoryRef(Backend: "redis"),
    Identity: new IdentityRef(Provider: "keycloak", Audience: "support-api"),
    Autoscaling: new AutoscalingSpec(MinReplicas: 1, MaxReplicas: 10, TargetConcurrencyPerReplica: 20),
    Labels: new Dictionary<string, string>
    {
        ["env"] = "staging",
        ["tier"] = "customer-facing",
    });

await registry.RegisterAsync(manifest);

var fetched = await registry.GetAsync("support-agent");          // returns latest version
var staging = await registry.ListAsync(labelPrefix: "env:staging");
```

## Manifest design notes

- **`AgentManifest` is a pure data record** — no behaviour, no validation callbacks, no DI fixtures. Consumers validate via their own layer; the registry stores whatever you give it.
- **`Name` + `Version` together uniquely identify** a manifest. Registry supports multiple versions side-by-side; `GetAsync(name, null)` returns the lexicographic-latest version.
- **`Tools` reference tools by `Source` string** — `"static"` for in-process, `"mcp:<server-name>"` for MCP discovery, `"a2a:<agent-name>"` for A2A delegation. The runtime resolves these at instantiation; consumers defining their own host also resolve via the `Source` string convention.
- **`Bindings` list protocols** — `"http"`, `"a2a"`, `"mcp"` — with per-binding config blobs. The cloud runtime reads these to wire ingress.
- **`Labels`** are free-form; used for filtering + routing.

## Verb semantics

The runtime implements `IAgentLifecycleManager` with these semantics:

| Verb | Semantics |
|---|---|
| `CreateAsync` | Provision a new instance from the manifest. Returns a durable `AgentHandle`. |
| `InvokeAsync` | Synchronous request/response. Equivalent to `StatefulAiAgent.AskAsync` on the remote side. |
| `SignalAsync` | Fire-and-forget event into the agent (HITL approval delivery, external triggers). |
| `QueryAsync` | Fetch current `AgentStatus` without side effects. |
| `CancelAsync` | Cancel in-progress work; preserves state. |
| `UpdateAsync` | Deploy a new `AgentManifest` version. |
| `EvictAsync` | Remove the instance and its state. |

These are surveyed universals; every target runtime (Bedrock AgentCore / Temporal / Restate / Dapr / OpenAI Assistants) expresses at least five of the seven with similar semantics.

## Extension points

- **`IAgentRegistry`** — file-backed, HTTP-backed, Kubernetes-CRD-backed implementations all fit the contract. Consumers who want Git-ops-style declarative registries build on top.
- **`IAgentLifecycleManager`** — the cloud runtime is one implementation; an Aspire-hosted local runtime is another valid one.
- **`IAgentIdentityProvider`** — per-tenant identity resolution + outbound credential acquisition. The runtime ships a Keycloak-backed impl; others (Azure AD, Okta) on the roadmap.

## Idempotency (v0.11)

Every mutating verb on the HTTP control plane takes an `Idempotency-Key` request header — Stripe-shape semantics. Duplicate calls with the same key replay the first response rather than re-executing.

- **Server-side state** lives in an `IIdempotencyStore` — `InMemoryIdempotencyStore` (ships in `Vais.Agents.Control.Http.Server`) or durable `OrleansIdempotencyStore` (in `Vais.Agents.Hosting.Orleans`).
- **Scoping key** is the 4-tuple `(tenantId, verbRoute, canonicalPath, idempotencyKey)`. Two tenants can reuse the same key; two verbs on the same agent are independent entries.
- **Body fingerprint** is a SHA-256 hex digest of the raw request body. Second call with the same key but different body → `422 urn:vais-agents:idempotency-mismatch`.
- **In-flight duplicate** → `409 urn:vais-agents:idempotency-in-flight` with a `Retry-After` header.
- **Replayed response** carries an `Idempotency-Replayed: true` header — clients can log or branch on it.
- **TTL** defaults to 24h (Stripe-aligned). Tunable via `IdempotencyOptions.Ttl`.

See [enable HTTP idempotency](../guides/enable-http-idempotency.md) for wiring + [problem-details URNs reference](../reference/problem-details-urns.md) for the full failure-shape table.

## OpenAPI (v0.11)

`GET /openapi/{documentName}.json` exposes an OpenAPI 3.1 document describing every mapped route + its request/response schemas + its error shapes. The `VaisProblemDetailsOperationTransformer` attaches an `x-vais-type-urns` extension to each error response — the URN array a caller may see for that status code, so codegen clients can branch on URN instead of parsing `detail` strings.

Wiring: `AddAgentControlPlaneOpenApi(documentName: "v1")` + `MapAgentControlPlaneOpenApi()`. Built on `Microsoft.AspNetCore.OpenApi 9.0.11`.

See [consume the OpenAPI spec](../guides/consume-the-openapi-spec.md) for codegen recipes (NSwag / Kiota / openapi-typescript).

## Streaming Invoke (v0.12)

Unary `POST /v1/agents/{id}/invoke` returns a single `AgentInvocationResult`. Streaming `POST /v1/agents/{id}/invoke/stream` returns a Server-Sent Events response carrying the **full `AgentEvent` hierarchy** — text deltas, tool dispatches, guardrail decisions, interrupts, handoffs — as distinct SSE frames.

| Aspect | Unary | Streaming |
|---|---|---|
| Route | `POST /v1/agents/{id}/invoke` | `POST /v1/agents/{id}/invoke/stream` |
| Content-type | `application/json` | `text/event-stream` |
| Body | `AgentInvocationResult` | Sequence of `AgentEvent` SSE frames (ten event names) |
| Idempotency middleware | Applies | Bypassed via `[StreamingEndpoint]` |
| Cancellation | Request-bounded | `HttpContext.RequestAborted` propagates into `StreamAsync` |

The route probes the resolved agent for `IStreamingAiAgent`; missing capability returns `501 urn:vais-agents:streaming-not-supported`. `StatefulAiAgent` implements it out of the box. `OrleansAiAgentProxy` also implements `IStreamingAiAgent` as of v0.35 — Orleans-hosted agents stream natively without falling back to 501.

Clients call either `InvokeStreamAsync` (text-only projection) or `InvokeStreamEventsAsync` (full events). See [stream invocations over HTTP](../guides/stream-invocations-over-http.md).

## Policy engines

Every verb on `IAgentLifecycleManager` flows through `IAgentPolicyEngine.EvaluateAsync` before reaching the runtime. The contract ships in `Vais.Agents.Control.Abstractions`; consumer wires one of:

- **`AllowAllPolicyEngine`** (Core default) — `Allow` on every call. Sane for dev + single-tenant hosts.
- **`OpaPolicyEngine`** (v0.14, `Vais.Agents.Control.Policy.Opa`) — delegates to an external OPA server over HTTP. Rego policies live in a ConfigMap or bundle server, decoupled from the runtime binary.
- **Custom `IAgentPolicyEngine`** — 30-line class reading a feature flag, a database table, a cached auth scope, whatever fits.

OPA is the right choice when you already run OPA for other admission tasks, need policy authoring outside the .NET engineering team, or want coherent policies that span multiple verbs (tenant + role + budget + model-allowlist). Skip OPA for pure in-process boolean checks or sub-millisecond latency budgets where even loopback HTTP adds too much.

See [OPA policy engine concept](opa-policy-engine.md) for the full wire contract, FailMode semantics, caching model, and the "4xx is a bug, 5xx is a policy path" classification rule.

## Observability

- `AgentStatus` transitions should emit events (design pending). For now consumers emit their own from any lifecycle-manager implementation.
- `AgentHandle` is the correlation primitive — thread it through every downstream log / trace / metric.

## Plugin introspection (v0.27)

`GET /v1/plugins` returns the list of loaded plugins and their current status. No dedicated CLI command wraps this endpoint; call it directly via `curl` or an HTTP client.

Response shape (JSON array):

```json
[
  { "name": "research-planner", "status": "Ready", "kind": "mcp-tool-server" },
  { "name": "langgraph-researcher", "status": "Ready", "kind": "agent-handler" }
]
```

Status values: `Loading`, `Ready`, `Error`, `Reloading`.

## Runtime topology discovery (v0.34)

`GET /v1/runtimes` returns the remote runtimes configured on this host. Credentials are excluded. Clients call this to enumerate available runtimes before constructing cross-runtime graph manifests.

CLI surface: `vais get-remote-runtimes`. See [CLI subcommands reference](../reference/cli-subcommands.md#vais-get-remote-runtimes).

## Limitations / known gaps

- **No multi-region** — manifest carries no region field. Multi-region support is on the roadmap.

## See also

- [Architecture](architecture.md)
- [Session + memory](session.md) — `MemoryRef` resolution.
- [Interop](interop.md) — `ProtocolBinding` targets.
- [Enable HTTP idempotency](../guides/enable-http-idempotency.md) — v0.11 walkthrough.
- [Consume the OpenAPI spec](../guides/consume-the-openapi-spec.md) — v0.11 walkthrough.
- [Stream invocations over HTTP](../guides/stream-invocations-over-http.md) — v0.12 walkthrough.
- [Problem-details URNs reference](../reference/problem-details-urns.md) — full URN table.
