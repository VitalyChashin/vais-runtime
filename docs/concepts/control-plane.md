# Control plane

Managed cloud runtimes (Bedrock AgentCore, Temporal, Restate, Dapr, OpenAI Assistants) converge on a universal verb set for agent lifecycles: **Create / Invoke / Signal / Query / Cancel / Update / Evict**. Vais.Agents ships those verbs as `IAgentLifecycleManager` + a value-typed `AgentManifest` that describes an agent deployment. v0.4 ships the **contract surface only** — no HTTP API, no CRDs, no YAML loader, no policy engine, no identity provider impl. Those land in Phase 3 (the cloud runtime).

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

No `IAgentLifecycleManager` impl. No `IAgentIdentityProvider` impl. Those are cloud-runtime (Phase 3) deliverables.

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
- **`Tools` reference tools by `Source` string** — `"static"` for in-process, `"mcp:<server-name>"` for MCP discovery, `"a2a:<agent-name>"` for A2A delegation. The cloud runtime (Phase 3) resolves these at instantiation; consumers defining their own runtime also resolve via the `Source` string convention.
- **`Bindings` list protocols** — `"http"`, `"a2a"`, `"mcp"` — with per-binding config blobs. The cloud runtime reads these to wire ingress.
- **`Labels`** are free-form; used for filtering + routing.

## Verb semantics (forward-looking)

The Phase 3 cloud runtime implements `IAgentLifecycleManager` with these semantics:

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
- **`IAgentIdentityProvider`** — per-tenant identity resolution + outbound credential acquisition. Phase 3 ships a Keycloak-backed impl; others (Azure AD, Okta) follow.

## Observability

- `AgentStatus` transitions should emit events (design pending). For now consumers emit their own from any lifecycle-manager implementation.
- `AgentHandle` is the correlation primitive — thread it through every downstream log / trace / metric.

## Limitations / known gaps

- **No HTTP API / CRDs / YAML loader in v0.4.** Cloud runtime (Phase 3).
- **No policy engine** — "which tenant can invoke which agent version" is a Phase 3 concern.
- **No multi-region** — manifest carries no region field. Phase 3 adds regionalisation.
- **No lifecycle-manager impl.** `IAgentLifecycleManager` ships contract-only.
- **No identity-provider impl.** `IAgentIdentityProvider` ships contract-only.

## See also

- [Architecture](architecture.md)
- [Session + memory](session.md) — `MemoryRef` resolution.
- [Interop](interop.md) — `ProtocolBinding` targets.
