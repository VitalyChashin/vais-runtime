# Interop

Two outbound adapters let a local Vais.Agents agent reach into the wider agent ecosystem:

- **MCP (`Vais.Agents.Protocols.Mcp`)** — pulls tools from an MCP server into the local `IToolRegistry`.
- **A2A (`Vais.Agents.Protocols.A2A`)** — wraps a remote A2A agent as a single `ITool`, so a local agent can delegate subtasks to a peer.

Both are outbound-only in v0.4 — the inbound counterparts (`McpAgentServer`, `A2AAgentEndpoint`) are deferred per the architectural review.

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
- **Outbound only.** `McpAgentServer` (expose our agent as MCP) deferred — "agent as MCP" maps poorly to MCP's tool / prompt / resource primitives.

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

- **Outbound only.** `A2AAgentEndpoint` (expose our agent as an A2A server) deferred — needs `A2A.AspNetCore` integration choices.
- **No A2A-provider bridge.** `A2ARemoteAgentProvider : ICompletionProvider` (use a remote agent as the completion stack) deferred — loses our event stream, session semantics. Revisit post-inbound.
- **No `OrleansTaskStore : ITaskStore`** — only relevant once we host A2A server-side.
- **TFM note.** `A2A 1.0.0-preview2` targets `net8.0` + `net10.0` only. Consumed under our `net9.0` via forward-compat; clean restore.

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

- MCP inbound + A2A inbound — both deferred.
- MCP multi-modal content — text only.
- A2A structured input — fixed `{message:string}` schema.
- A2A remote-as-provider — deferred.

## See also

- [Architecture](architecture.md)
- [Tools](tools.md) — `ITool` + `IToolSource` + `AggregatingToolRegistry`.
- [Execution loop](execution-loop.md) — dispatcher semantics.
- [Expose MCP tools to an agent guide](../guides/expose-mcp-tools-to-an-agent.md)
- [Delegate to an A2A remote agent guide](../guides/delegate-to-a2a-remote-agent.md)
