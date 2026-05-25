# Interop

Four adapters let Vais.Agents reach across protocol boundaries in both directions:

**Outbound** (local agent calls remote):
- **MCP (`Vais.Agents.Protocols.Mcp`)** — pulls tools from an MCP server into the local `IToolRegistry`.
- **A2A (`Vais.Agents.Protocols.A2A`)** — wraps a remote A2A agent as a single `ITool`, so a local agent can delegate subtasks to a peer.

**Inbound** (local agent is called by remote, shipped in v0.7 + v0.8):
- **MCP server (`Vais.Agents.Protocols.Mcp.Server`)** — exposes every registered agent as one MCP tool over stdio or streamable-HTTP. Manifests double as MCP resources.
- **A2A server (`Vais.Agents.Protocols.A2A.Server`)** — hosts registered agents as A2A endpoints under `/agents/{id}` with auto-derived `AgentCard`s. `OrleansTaskStore` (in `Vais.Agents.Hosting.Orleans`) makes `input-required` tasks durable across silo restart.

## Why only these two protocols

The prior-art survey on interop (seven protocols) converged on MCP + A2A as the two worth adapter investment:

- **Model Context Protocol** — GA-stable; shipped .NET SDK (`ModelContextProtocol.Core 1.2.0`); growing adoption across IDE and agent vendors.
- **Agent-to-Agent protocol** — GA-stable; shipped .NET SDK (`A2A 1.0.0-preview2`); interoperable with MAF's native `AIAgent.MapA2A` + other A2A-speaking runtimes.

Everything else (NLIP, AGNTCY ACP, Agent Protocol, IBM ACP) is speculative, merged into A2A, or abandoned. Polyglot agent interop (Python, TS) emerges for free via A2A alignment — no VAIS-specific wire protocol.

## MCP — outbound tool source

`McpToolSource` wraps a pre-connected `McpClient`. The caller owns the connection lifecycle (stdio, streamable-HTTP, etc.) because MCP transports vary and connection setup is scenario-specific. The source itself is stateless + testable.

```csharp
using ModelContextProtocol.Client;
using Vais.Agents.Protocols.Mcp;
using Vais.Agents.Core;

// Caller builds + connects the client.
await using var mcpClient = await McpClient.CreateAsync(
    new ClientTransport(/* stdio / streamable-HTTP config */));

// Wrap as IToolSource, feed into AggregatingToolRegistry.
IToolSource mcpSource = new McpToolSource(mcpClient);

var registry = await AggregatingToolRegistry.BuildAsync(
    staticTools: Array.Empty<ITool>(),
    sources: new[] { mcpSource });

var agent = new StatefulAiAgent(provider, new StatefulAgentOptions { ToolRegistry = registry });
```

### What the source yields

Each MCP tool surfaces as an `McpBackedTool : ITool` (internal). Name, description, and JSON-schema parameters come directly from the MCP `Tool` metadata. `InvokeAsync`:

1. Converts the agent's `JsonElement` arguments into a dict MCP accepts.
2. Calls `mcpClient.CallToolAsync(name, args, progress: null, options: ..., ct)`.
3. Concatenates all `TextContentBlock`s from `CallToolResult.Content` with `\n` separators.
4. Throws `McpToolInvocationException` if `CallToolResult.IsError == true`.

### v0.4 limitations

- **Text-only content concatenation.** Image / audio / resource content blocks are ignored. Tools that return mixed-modal responses lose their non-text parts. Revisit when a consumer needs multi-modal tool output.

## MCP — inbound (agents as MCP tools)

Shipped in v0.7 as `Vais.Agents.Protocols.Mcp.Server`. One MCP tool per registered agent id — the "agent as MCP" semantic settled on tool-per-agent after prior rounds considered prompt-per-agent and rejected it (prompts have no invocation surface).

```csharp
using Microsoft.Extensions.Hosting;
using Vais.Agents.Protocols.Mcp.Server;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddAgenticInMemoryHosting();
builder.Services.AddInProcessAgentControlPlane();
builder.Services.AddMcpAgentServerStdio(o => o.Name = "my-agents-mcp");
await builder.Build().RunAsync();
```

Two shipped transports:

- **stdio** via `AddMcpAgentServerStdio` + `StdioAgentServerHost` (`BackgroundService`). Claude Desktop spawns the executable and speaks MCP over its stdio.
- **streamable-HTTP** via `AddMcpAgentServerHttp` + `MapMcpAgentServer("/mcp")` on ASP.NET Core. Web deployments or gateway composition (ContextForge, etc.).

Each agent becomes an MCP tool with:

- Name = agent id (sanitised to `[A-Za-z0-9_-]+`)
- Description = manifest `Description`
- Input schema = fixed `{ message: string }` — mirrors the A2A-outbound convention (no universal structured input at the agent level)
- Handler = `IAgentLifecycleManager.InvokeAsync`
- Output = `TextContentBlock` carrying the assistant turn

Manifests additionally publish as MCP **resources** at `agent://{id}/{version}/manifest` (content-type `application/json`; body is the v0.6 control-plane envelope). Clients that want to introspect before calling can `resources/read` first.

`LabelPrefixFilter` in `McpAgentServerOptions` constrains which registered agents appear as tools — multi-tenant hosts typically expose a subset per MCP endpoint.

Optional `AddMcpAgentServerJwtAuth` fronts the HTTP transport with bearer auth (stdio transport doesn't authenticate — the client spawned the process).

### v0.7 limitations

- **Text content only** — `TextContentBlock` responses, no image / audio / resource.
- **No streaming** — MCP `tools/call` is unary. For streamed turns use the v0.12 SSE endpoint on the HTTP control plane instead.
- **No `prompts` / `sampling` roles** — tools + resources only.

## A2A — remote agent as a tool

`A2ARemoteAgentTool` implements `ITool` by wrapping an `IA2AClient` + an `AgentCard`. Static `CreateAsync(Uri)` factory resolves the remote's card via `A2ACardResolver`, builds the client, and constructs the tool.

```csharp
using Vais.Agents.Protocols.A2A;

var remote = await A2ARemoteAgentTool.CreateAsync(
    new Uri("https://peer-agent.example.com/a2a"),
    httpClient: null);   // let the SDK manage its own HttpClient

// The remote's AgentCard.Name becomes the tool name (sanitised to match [A-Za-z0-9_-]+).
// The remote's AgentCard.Description becomes the tool description.
// Fixed input schema: { "type":"object", "properties":{ "message":{"type":"string"} }, "required":["message"] }

var agent = new StatefulAiAgent(
    provider,
    new StatefulAgentOptions { ToolRegistry = new InMemoryToolRegistry(remote) });

Console.WriteLine(await agent.AskAsync("Ask the weather-bot peer for the forecast in Paris."));
```

### How the invocation works

1. Local agent's model decides to call the remote (tool surfaced as any other `ITool`).
2. `A2ARemoteAgentTool.InvokeAsync` extracts `arguments.message` (string), wraps it in an A2A `Message` + `SendMessageRequest`, calls `_client.SendMessageAsync`.
3. Response is `SendMessageResponse` — discriminated union:
   - `PayloadCase == Message` → concatenate all `TextContentBlock`s from the `Message.Parts`.
   - `PayloadCase == Task` → concatenate text blocks across every `AgentTask.Artifacts[].Parts`.
   - Otherwise → throw `A2AAgentInvocationException`.
4. Return the concatenated text as the tool's result.

The agent's outer loop receives the result as a normal `ToolCallOutcome.Result` and continues its iteration.

### Input schema rationale

A2A's `AgentCard` describes skills but not a single "input shape" — a tool-call model just needs one string to send. We fix the schema at `{message:string}` rather than trying to derive per-skill shapes. Callers wanting structured sub-agent I/O can wrap their own adapter.

### Tool-name sanitisation

`AgentCard.Name` is free-form UTF-8; `ITool.Name` must match `[A-Za-z0-9_-]+`. The wrapper sanitises — non-matching chars map to `_`, runs collapse, trim — and throws `ArgumentException` if the result is empty. `AgentName` property preserves the original (unsanitised) name for display.

### v0.4 limitations

- **TFM note.** `A2A 1.0.0-preview2` targets `net8.0` + `net10.0` only. Resolves to its native `net10.0` target under our TFM; clean restore.

## A2A — inbound (agents as A2A endpoints)

Shipped in v0.8 as `Vais.Agents.Protocols.A2A.Server`. Each registered agent becomes an A2A endpoint at `{BasePath}/{id}` with an auto-derived `AgentCard` published at `{BasePath}/{id}/.well-known/agent-card.json`.

```csharp
using Microsoft.AspNetCore.Builder;
using Vais.Agents.Protocols.A2A.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAgenticInMemoryHosting();
builder.Services.AddInProcessAgentControlPlane();
builder.Services.AddA2AAgentServer(o => o.BasePath = "/agents");

var app = builder.Build();
app.MapA2AAgentServer(baseUrl: "https://your-public-host.example.invalid");
app.Run();
```

### Auto-derived `AgentCard`

`AgentCardBuilder` translates a manifest into an A2A `AgentCard` with sensible defaults — `Id` → `Name`, `Description` → card description, `ProviderOrganization` (from options) → `Provider.Organization`, every manifest `ProtocolBinding { Kind = "A2A" }` → a card `Interface`. Three override hooks in `A2AAgentServerOptions`:

- `CustomizeCard: Action<AgentManifest, AgentCard>` — post-process every auto-derived card (tags, description tweaks, appended skills).
- `BuildCard: Func<AgentManifest, AgentCard>` — replace the auto-default globally.
- `PerAgentOverrides: Dictionary<id, Func<...>>` — hand-author one specific agent's card while leaving the rest auto-derived.

Precedence: per-agent > global builder > auto-default → then `CustomizeCard` runs (unless per-agent fired).

### Interrupts → A2A `Task(input-required)`

A2A's `SendMessageResponse` is a discriminated union of `Message` | `Task`. The server maps our `AgentInterrupt` to the `Task(input-required)` shape:

1. Peer posts `message/send` to `POST {BasePath}/{id}`.
2. Handler invokes `IAgentLifecycleManager.InvokeAsync`.
3. On `AgentInterruptedException`, server wraps the interrupt in an `AgentTask { Status: InputRequired, ContextId: <interruptId> }` and stores it via `ITaskStore`.
4. Peer resumes with a fresh `message/send` carrying `taskId = <contextId>`; handler runs `SignalAsync(kind: "resume")` on the original run.

### `OrleansTaskStore` for durable HITL

Default `InMemoryTaskStore` loses in-flight tasks on process restart. `Vais.Agents.Hosting.Orleans` ships `OrleansTaskStore : A2A.ITaskStore` that serialises every task to an `A2ATaskGrain` keyed by `taskId` — the A2A `input-required` round-trip survives silo restart, days or weeks of downtime, and grain-storage migration.

```csharp
builder.Services.AddOrleansA2ATaskStore();   // must run BEFORE AddA2AAgentServer
builder.Services.AddA2AAgentServer(o => { ... });
```

### JWT auth under a dedicated scheme

`AddA2AAgentServerJwtAuth` registers bearer-token validation under the `A2AJwt` scheme — distinct from the v0.6 control-plane `Bearer` scheme, so a single host can mix authenticated A2A-public + unauthenticated internal traffic. Mapped `AgentPrincipal` flows through to the policy engine.

### v0.8 limitations

- **Unary `message/send` only.** SSE streaming (A2A's server-streaming variant) deferred — reuse the v0.12 control-plane SSE endpoint if you need streamed turns across A2A.
- **No A2A-provider bridge.** `A2ARemoteAgentProvider : ICompletionProvider` (using a remote agent as the local completion stack) stays deferred — loses event-stream + session semantics.
- **`baseUrl` placeholder.** When `MapA2AAgentServer` is called without a baseUrl and behind an unknown reverse proxy, the card's `Interface.Url` falls back to a localhost placeholder; downstream consumers must rewrite it.

## Composing MCP + A2A + static tools

```csharp
var registry = await AggregatingToolRegistry.BuildAsync(
    staticTools: new[] { new RollDiceTool() },
    sources: new IToolSource[]
    {
        new McpToolSource(mcpClient),
        // A2A doesn't have an IToolSource — it's per-remote, add explicit tools:
    });

// Add A2A remotes as static tools (or build a custom IToolSource that enumerates your peer registry):
var weatherPeer = await A2ARemoteAgentTool.CreateAsync(weatherUri);
var combined = new InMemoryToolRegistry(registry.Tools.Concat(new[] { weatherPeer }).ToArray());
```

Whatever the model picks, the agent's outer loop dispatches uniformly through `IToolCallDispatcher`.

## Extension points

- **Custom `IToolSource`** — plug in any tool-discovery mechanism (GraphQL schema introspection, plugin DLLs, a proprietary catalogue).
- **Custom `ITool`** wrapping another transport — REST, gRPC, vendor-specific MCP-alikes. If you can call it with a `JsonElement` and return a string, it's a tool.

## Observability

- Every MCP / A2A call is a normal tool dispatch → emits `ToolCallStarted` + `ToolCallCompleted` events, counts against `RunBudget.MaxToolCalls`, runs tool guardrails.
- `McpToolInvocationException` / `A2AAgentInvocationException` surface as `ToolCallOutcome.Error` and feed back to the model — same semantics as any other tool failure.

## Limitations / known gaps summary

- MCP outbound multi-modal content — text only.
- A2A outbound structured input — fixed `{message:string}` schema.
- MCP inbound content modes — text only; no streaming tool responses; no `prompts` / `sampling` roles.
- A2A inbound streaming — deferred; unary `message/send` only.
- A2A remote-as-provider — deferred.

## See also

- [Architecture](architecture.md)
- [Tools](tools.md) — `ITool` + `IToolSource` + `AggregatingToolRegistry`.
- [Execution loop](execution-loop.md) — dispatcher semantics.
- [Expose MCP tools to an agent guide](../guides/expose-mcp-tools-to-an-agent.md) — outbound MCP.
- [Delegate to an A2A remote agent guide](../guides/delegate-to-a2a-remote-agent.md) — outbound A2A.
- [Host agents as MCP tools guide](../guides/host-agents-as-mcp-tools.md) — inbound MCP (v0.7).
- [Host agents as A2A endpoints guide](../guides/host-agents-as-a2a-endpoints.md) — inbound A2A (v0.8).
