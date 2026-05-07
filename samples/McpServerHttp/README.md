# McpServerHttp

Host a `StatefulAiAgent` as an MCP tool over **streamable-HTTP** (`POST /mcp`). Boots an ASP.NET Core server, connects a co-located `McpClient` via `HttpClientTransport`, and runs a scripted tools/list → tools/call → resources/list sequence.

## Run

```bash
dotnet run --project samples/McpServerHttp
```

## Expected output

```
Server: http://127.0.0.1:54321/mcp

tools/list → 1 tool(s):
  • greeter — greeter (v1.0) — A friendly greeter agent.

tools/call greeter { text: "Hello!" }:
  → "Hello! I'm the greeter agent. How can I help?"

resources/list → 1 resource(s):
  • agent://greeter/1.0/manifest — A friendly greeter agent.
```

*(Port number varies.)*

## What it demonstrates

- `AddMcpAgentServerHttp()` — wires the MCP SDK's `AddMcpServer().WithHttpTransport()` with Vais.Agents handlers so the same `IAgentRegistry` / `IAgentLifecycleManager` routing works over HTTP.
- `MapMcpAgentServer("/mcp")` — mounts the streamable-HTTP + SSE route pair at `/mcp` per the MCP 2025-06-18 spec.
- `HttpClientTransport` (`ModelContextProtocol.Core`) — the client-side counterpart; handles the initialize handshake, session token, and SSE response streaming.
- `IAgentRegistry` resolved per-request — `tools/list` reflects live registry state.
- Same tool schema as `McpServerStdio`: `{ text, sessionId?, resume? }`.

## Production extension

Swap the scripted provider for a real one, add `AddMcpAgentServerJwtAuth(o => ...)` + `UseAuthentication()` / `UseAuthorization()` for token-protected endpoints, and register additional `AgentManifest` entries before starting.

## Docs

- [Host agents as MCP tools](../../docs/guides/host-agents-as-mcp-tools.md)
- [`McpServerStdio`](../McpServerStdio) — same agent over stdio (Claude Desktop transport)
