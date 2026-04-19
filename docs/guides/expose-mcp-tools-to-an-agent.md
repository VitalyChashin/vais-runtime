# Guide: expose MCP tools to an agent

Pull tools from an MCP server into the agent's `IToolRegistry` via `McpToolSource`. The caller owns the `McpClient` connection; the source wraps it as an `IToolSource` that feeds `AggregatingToolRegistry.BuildAsync`.

## Packages

```xml
<PackageReference Include="Vais.Agents.Protocols.Mcp" Version="0.4.0-preview" />
<PackageReference Include="ModelContextProtocol.Core" Version="1.2.0" />
```

`ModelContextProtocol.Core` ships client + low-level server types only — smaller transitive graph than the full `ModelContextProtocol` metapackage (which pulls `Microsoft.Extensions.Hosting.Abstractions` + `Caching.Abstractions`). We don't need those.

## Connect to an MCP server

Transport + connection are the caller's job — MCP servers commonly use **stdio** (launch a child process) or **streamable HTTP** (long-lived HTTP POST). Example over stdio:

```csharp
using ModelContextProtocol.Client;

await using var client = await McpClient.CreateAsync(new ClientTransportOptions
{
    Name = "my-mcp-server",
    TransportType = "stdio",
    Command = "node",
    Arguments = new[] { "./mcp-server.js" },
});
```

(Adjust for your server's transport. See the MCP SDK docs for streamable-HTTP specifics.)

## Wrap as `IToolSource`

```csharp
using Vais.Agents;
using Vais.Agents.Core;
using Vais.Agents.Protocols.Mcp;

IToolSource mcpSource = new McpToolSource(client);

var registry = await AggregatingToolRegistry.BuildAsync(
    staticTools: Array.Empty<ITool>(),
    sources: new[] { mcpSource });
```

`BuildAsync` enumerates every source once, freezes the tool list, and returns an `IToolRegistry` with a sync `Tools` getter. Re-run if the MCP server's tool catalogue changes — discovery is not automatic.

## Use

```csharp
var agent = new StatefulAiAgent(
    completionProvider,
    new StatefulAgentOptions { ToolRegistry = registry });

Console.WriteLine(await agent.AskAsync("Use the available tools to look up X."));
```

When the model calls an MCP-backed tool, the agent's outer loop dispatches through `DefaultToolCallDispatcher` — same path as any other tool. Events (`ToolCallStarted` / `ToolCallCompleted`), guardrails (`IToolGuardrail`), budget (`RunBudget.MaxToolCalls`) all apply uniformly.

## What the adapter does per call

1. Receives `ToolCallRequest(toolName, arguments, callId)` from the outer loop.
2. Converts `arguments` (a `JsonElement`) into a `Dictionary<string, object?>` MCP accepts.
3. Calls `client.CallToolAsync(toolName, args, progress: null, options: …, ct)`.
4. Concatenates all `TextContentBlock`s from the returned `CallToolResult.Content` with `\n` separators.
5. Throws `McpToolInvocationException` if `CallToolResult.IsError == true`.
6. Returns the concatenated text as the tool result.

## v0.4 limitations

- **Text-only content.** `ImageContentBlock` / `AudioContentBlock` / resource blocks in tool results are ignored. Tools returning mixed-modal responses lose the non-text parts. Workaround: write a custom `ITool` wrapper that knows your mixed-modal shape.
- **Outbound only.** No `McpAgentServer` — exposing the agent as an MCP server has unresolved semantic questions (MCP's tool / prompt / resource primitives don't map cleanly to "call this agent as a unit"). Deferred.
- **Pre-connected client.** `McpToolSource` wraps an already-connected `McpClient` at construction. If the MCP server goes away mid-run, `CallToolAsync` fails; wrap with retry / circuit-break at your application layer.

## Things that catch people

- **Pick `ModelContextProtocol.Core`, not the metapackage.** The MCP 1.2 split moved hosting + DI helpers into the outer `ModelContextProtocol` package. For just-the-client-wrapping, `.Core` is leaner.
- **Stream APIs renamed.** MCP 1.2 changed `EnumerateToolsAsync` to `ListToolsAsync` (eager; SDK auto-paginates) and `CallToolResponse` to `CallToolResult`. Not a consumer concern for this guide — `McpToolSource` already tracks the shape — but know it if you read older MCP sample code.
- **Anon call-ids.** Some MCP servers return `null` call ids; our adapter is resilient to that for *response* accumulation, but if you log raw call-ids make sure to handle the null case.

## See also

- [Interop concept](../concepts/interop.md)
- [Tools concept](../concepts/tools.md) — `IToolSource` + `AggregatingToolRegistry`.
- [Delegate to an A2A remote agent](delegate-to-a2a-remote-agent.md) — sibling pattern for A2A.
- Sample: `samples/McpToolSourceExample/` (per samples plan)
