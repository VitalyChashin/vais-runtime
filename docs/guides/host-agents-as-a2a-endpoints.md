# Guide: host agents as A2A endpoints

Expose registered agents as Agent-to-Agent endpoints under `/agents/{id}`. Each agent auto-gets an `AgentCard` published at `.well-known/agent-card.json`, so A2A directory services and peer agents can discover + invoke your agents using the standard protocol. Interrupts surface as A2A `Task(input-required)`; resume happens via `taskId`.

Shipped in v0.8 as `Vais.Agents.Protocols.A2A.Server`, with `OrleansTaskStore` in `Vais.Agents.Hosting.Orleans` for durable HITL.

## Packages

Add to your host project:

```xml
<PackageReference Include="Vais.Agents.Abstractions" Version="0.15.0-preview" />
<PackageReference Include="Vais.Agents.Core" Version="0.15.0-preview" />
<PackageReference Include="Vais.Agents.Hosting.InMemory" Version="0.15.0-preview" />
<PackageReference Include="Vais.Agents.Protocols.A2A.Server" Version="0.15.0-preview" />
<!-- Optional, for durable input-required tasks across process restart: -->
<PackageReference Include="Vais.Agents.Hosting.Orleans" Version="0.15.0-preview" />
```

## Minimal host

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Vais.Agents;
using Vais.Agents.Control;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Hosting.InMemory;
using Vais.Agents.Protocols.A2A.Server;

var builder = WebApplication.CreateBuilder(args);

// In-memory runtime + control plane + one demo agent.
builder.Services.AddAgenticInMemoryHosting();
builder.Services.AddInProcessAgentControlPlane();
builder.Services.AddSingleton<IAgentRegistry>(sp =>
{
    var registry = new InMemoryAgentRegistry();
    registry.Register(new AgentManifest(
        Id: "weather",
        Version: "v1",
        Handler: new AgentHandlerRef("MyApp.WeatherAgent"),
        Protocols: new[] { new ProtocolBinding("A2A") },
        Tools: Array.Empty<ToolRef>())
    {
        Description = "Answers questions about the weather.",
    });
    return registry;
});

builder.Services.AddA2AAgentServer(o =>
{
    o.BasePath = "/agents";
    o.ProviderOrganization = "example-inc";
});

var app = builder.Build();
app.MapA2AAgentServer(baseUrl: "https://your-public-host.example.invalid");
app.Run();
```

After startup, every registered agent gets two routes:

- `POST /agents/{id}` — A2A `message/send`. Body is a standard A2A `SendMessageRequest`; response is a `SendMessageResponse` carrying either a direct `Message` or a `Task` (for interrupts).
- `GET /agents/{id}/.well-known/agent-card.json` — auto-derived agent card. Manifest `Id` → card `Name`, `Description` → card `Description`, `ProviderOrganization` from options → card `Provider.Organization`, `Protocols[].Kind == "A2A"` feeds `Interfaces[]`.

## Customising agent cards

Defaults are sane. Override when you need to:

```csharp
builder.Services.AddA2AAgentServer(o =>
{
    o.BasePath = "/agents";

    // Hook 1: post-process every auto-derived card.
    o.CustomizeCard = (manifest, card) =>
    {
        card.Tags.Add("tier:gold");
        card.Description = $"Curated: {card.Description}";
    };

    // Hook 2: replace the auto-default for a specific agent entirely.
    o.PerAgentOverrides = new Dictionary<string, Func<AgentManifest, AgentCard>>
    {
        ["chat-concierge"] = manifest => HandAuthoredCardFor(manifest),
    };

    // Hook 3: swap out the default builder globally (rare).
    // o.BuildCard = myCustomBuilder;
});
```

Precedence: `PerAgentOverrides` > `BuildCard` > auto-default → then `CustomizeCard` runs (unless `PerAgentOverrides` fired).

## Interrupts → `Task(input-required)`

A2A's message-send semantics naturally map to our `AgentInterrupt`:

1. Peer calls `POST /agents/weather` with a user message.
2. Handler invokes `IAgentLifecycleManager.InvokeAsync`.
3. Tool guardrail (or any code path) throws `AgentInterruptedException`.
4. Server wraps the interrupt in an A2A `AgentTask { Status: TaskStatus.InputRequired, ContextId: <interruptId> }` and stores it via `ITaskStore`.
5. Response carries that `Task` instead of a `Message`.

The peer then resumes by posting another `message/send` with `taskId = <contextId>` — the server looks up the pending task, feeds the response through `IAgentLifecycleManager.SignalAsync` (kind = `"resume"`), and continues the run.

## Durable `OrleansTaskStore` — input-required survives silo restart

Default `InMemoryTaskStore` loses in-flight tasks when the process dies. Production wants durability — swap in `OrleansTaskStore`:

```csharp
using Vais.Agents.Hosting.Orleans;

// Wire the Orleans silo first (see the "run on orleans locally" guide).
builder.Services.AddOrleansA2ATaskStore();      // registers ITaskStore before AddA2AAgentServer
builder.Services.AddA2AAgentServer(o => { ... });
```

The store serialises every A2A task to an `A2ATaskGrain` keyed by `taskId`; grain persistence flows through your configured Redis / Postgres provider. A silo restart reconstitutes `input-required` tasks exactly — the peer can resume days or weeks later.

Ordering note: `AddOrleansA2ATaskStore` uses `TryAddSingleton`, so it must run **before** `AddA2AAgentServer` (which would otherwise win with its in-memory default).

## JWT auth

Front the A2A routes with bearer-token auth:

```csharp
builder.Services.AddA2AAgentServerJwtAuth(opts =>
{
    opts.Authority = "https://your-issuer";
    opts.Audience = "vais-agents-runtime";
});
```

Registration mounts a dedicated `A2AJwt` scheme (distinct from the v0.6 control-plane `Bearer` scheme) so one host can serve unauthenticated internal traffic on control-plane routes while gating A2A externally. The mapped `AgentPrincipal` flows through to the policy engine the same way v0.6 does.

## Filter which agents you publish

`LabelPrefixFilter` constrains which manifests the server discovers:

```csharp
builder.Services.AddA2AAgentServer(o =>
{
    o.LabelPrefixFilter = "exposed:public";   // only agents with label exposed=public appear as A2A endpoints
});
```

Same mechanic as v0.7 MCP inbound + v0.6 control plane — all three use the `IAgentRegistry.ListAsync(labelPrefix)` contract.

## Limitations

- **Unary `message/send` only in v0.8.** SSE streaming (A2A's server-streaming variant) deferred — reuse the v0.12 control-plane SSE endpoint if you need streaming to A2A peers.
- **No A2A-provider bridge.** `A2ARemoteAgentProvider : ICompletionProvider` (use a remote A2A agent as your local agent's completion stack) deferred — loses our event stream + session semantics.
- **Card `Url` auto-placeholder when no `baseUrl` supplied.** When reverse-proxied behind an unknown public hostname, either pass the hostname explicitly to `MapA2AAgentServer(baseUrl: "...")` or have the downstream consumer rewrite the URL.

## See also

- [Interop concept](../concepts/interop.md) — A2A inbound section + outbound `A2ARemoteAgentTool` symmetry.
- [Delegate to an A2A remote agent](delegate-to-a2a-remote-agent.md) — v0.4 outbound counterpart.
- [Host agents as MCP tools](host-agents-as-mcp-tools.md) — v0.7 MCP inbound (parallel shape).
- [Run on Orleans locally](run-on-orleans-locally.md) — prerequisite for `OrleansTaskStore` durability.
- [`samples/A2AServerBasics`](../../samples/A2AServerBasics) + [`samples/A2AInterruptResumeOrleans`](../../samples/A2AInterruptResumeOrleans) — runnable walkthroughs.
