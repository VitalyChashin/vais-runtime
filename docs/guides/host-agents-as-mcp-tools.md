# Guide: host agents as MCP tools

Expose one or more registered agents as MCP tools so any MCP-speaking client — Claude Desktop, a ContextForge gateway, a custom client — can invoke them over the standard protocol. One shipped agent id becomes one MCP tool. The manifest is also published as an MCP resource at `agent://{id}/{version}/manifest` so clients can inspect it before calling.

Shipped in v0.7 as `Vais.Agents.Protocols.Mcp.Server`.

## Packages

Add to your host project:

```xml
<PackageReference Include="Vais.Agents.Abstractions" Version="0.15.0-preview" />
<PackageReference Include="Vais.Agents.Core" Version="0.15.0-preview" />
<PackageReference Include="Vais.Agents.Hosting.InMemory" Version="0.15.0-preview" />
<PackageReference Include="Vais.Agents.Protocols.Mcp.Server" Version="0.15.0-preview" />
```

## Pick a transport

MCP specifies two transports. The shipped server supports both; pick per deployment:

| Transport | Use when | DI extension |
|---|---|---|
| **stdio** | Launched as a subprocess by an IDE-class client (Claude Desktop, code editors). Server reads stdin, writes stdout. | `AddMcpAgentServerStdio` |
| **streamable-HTTP** | Web deployment. Client posts to a `POST /mcp` route on an ASP.NET Core host. | `AddMcpAgentServerHttp` + `MapMcpAgentServer` |

Either way the agent-side wiring is identical — only the host shape differs.

## Stdio: Claude-Desktop-spawnable executable

Build a tiny console host that registers one or more agents in an `IAgentRegistry` and hands them to the stdio server. Claude Desktop spawns your executable when it needs the tool.

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Vais.Agents;
using Vais.Agents.Control;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Hosting.InMemory;
using Vais.Agents.Protocols.Mcp.Server;

var builder = Host.CreateApplicationBuilder(args);

// Register the in-memory runtime + your agents.
builder.Services.AddAgenticInMemoryHosting();
builder.Services.AddInProcessAgentControlPlane();

// Add one manifest (usually from YAML in production; inline here).
builder.Services.AddSingleton<IAgentRegistry>(sp =>
{
    var registry = new InMemoryAgentRegistry();
    registry.Register(new AgentManifest(
        Id: "weather",
        Version: "v1",
        Handler: new AgentHandlerRef("MyApp.WeatherAgent"),
        Protocols: new[] { new ProtocolBinding("Http") },
        Tools: Array.Empty<ToolRef>())
    {
        Description = "Answers questions about the weather.",
    });
    return registry;
});

// Register the MCP stdio server. AddHostedService<StdioAgentServerHost> drives the loop.
builder.Services.AddMcpAgentServerStdio(o =>
{
    o.Name = "weather-mcp";
    o.Version = "1.0.0";
    o.Instructions = "Call the 'weather' tool with a natural-language question.";
});

await builder.Build().RunAsync();
```

Hand Claude Desktop a config pointing at the built executable:

```json
{
  "mcpServers": {
    "weather": {
      "command": "/path/to/your/host.exe"
    }
  }
}
```

Claude spawns the host on first tool invocation, speaks MCP over its stdio, and tears it down when the conversation ends.

## Streamable-HTTP: web deployment

For a long-running service behind a reverse proxy:

```csharp
using Microsoft.AspNetCore.Builder;
using Vais.Agents.Protocols.Mcp.Server;

var builder = WebApplication.CreateBuilder(args);

// ...same agent + registry + control-plane registrations as above...

builder.Services.AddMcpAgentServerHttp(o =>
{
    o.Name = "weather-mcp";
    o.Version = "1.0.0";
});

// Optional: JWT auth in front of MCP routes.
// builder.Services.AddMcpAgentServerJwtAuth(opt => opt.Authority = "https://your-issuer");

var app = builder.Build();
app.MapMcpAgentServer("/mcp"); // POST /mcp
app.Run();
```

Clients POST to `/mcp` with the MCP JSON-RPC envelope. Behind the scenes the same `McpAgentServerBuilder` wires each registered agent to a tool handler; the HTTP layer is just a different transport over the same core.

## Semantic: one tool per agent id

Every manifest in `IAgentRegistry` becomes one MCP tool. The tool name is the agent id (sanitised to `[A-Za-z0-9_-]+`), the description is the manifest's `Description`, and the input schema is fixed at `{ message: string }` — the MCP client passes natural-language text, the server runs it through `IAgentLifecycleManager.InvokeAsync`, and the result text comes back as an MCP `TextContentBlock`.

This mirrors the A2A-outbound-wrapping convention from v0.4: agent-level dispatch doesn't have a universally structured input shape, so we settle on a single string. Consumers wanting structured sub-agent I/O wrap their own adapter.

## Manifests as MCP resources

In addition to tools, the server publishes every manifest as a resource at `agent://{id}/{version}/manifest`. Content-type is `application/json`; body is the v0.6 control-plane JSON envelope. Clients that want to introspect before calling (model-card, budget, guardrail metadata) can `resources/read` the manifest first.

## Filter which agents you expose

Multi-tenant deployments often don't want every registered agent visible on every MCP endpoint. Use `LabelPrefixFilter` to publish a subset:

```csharp
builder.Services.AddMcpAgentServerStdio(o =>
{
    o.LabelPrefixFilter = "team:platform";  // only agents with a team=platform label appear as MCP tools
});
```

Applied at registry-list time — matches the v0.6 `IAgentRegistry.ListAsync(labelPrefix)` contract.

## Auth

stdio transport doesn't authenticate (the client spawned the process). HTTP transport optionally fronts JWT via `AddMcpAgentServerJwtAuth` — same `AddJwtBearer` shape as the v0.6 control plane. Every inbound tool call carries the principal through to the policy engine.

## Limitations

- **Text content only.** The server returns `TextContentBlock`s — image / audio / resource blocks are not emitted. Matches the v0.4 outbound adapter's symmetric limitation.
- **No streaming tool responses.** MCP's `tools/call` is unary; the inbound server doesn't split long agent turns into streamed chunks. For streaming, use the v0.12 SSE endpoint on the HTTP control plane.
- **No prompts / sampling roles.** Server exposes tools + resources only. MCP's `prompts` and `sampling` are out of scope for v0.7.

## See also

- [Interop concept](../concepts/interop.md) — MCP inbound section + outbound `McpToolSource` symmetry.
- [Delegate to an A2A remote agent](delegate-to-a2a-remote-agent.md) — v0.4 outbound A2A.
- [Host agents as A2A endpoints](host-agents-as-a2a-endpoints.md) — v0.8 A2A inbound counterpart.
- `samples/McpServerStdio` + `samples/McpServerHttp` — runnable walkthroughs (pending — see [samples plan](../../plans/actor-agents-oss-housekeeping-samples-plan.md)).
