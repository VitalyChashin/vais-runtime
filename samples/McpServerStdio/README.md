# McpServerStdio

Host a `StatefulAiAgent` as an MCP tool over **stdio**, the transport Claude Desktop uses when it spawns an MCP server as a child process.

## Run

```bash
# Demo — deterministic, no API key, exits after printing tool schema + scripted response:
dotnet run --project samples/McpServerStdio -- --demo

# Server mode — connect from Claude Desktop or any MCP stdio client:
dotnet run --project samples/McpServerStdio
```

## Expected output (`--demo`)

```
=== McpServerStdio — demo ===

tools/list (one MCP tool per registered agent):
  tool:  greeter v1.0 — A friendly greeter agent.
  input: { "text": string (required), "sessionId"?: string, "resume"?: { interruptId, ... } }

tools/call  name=greeter  arguments={ text: "Hi!" }:
  → "Hello! I'm the greeter agent. How can I help?"

resources/list (manifest URI per agent):
  agent://greeter/1.0/manifest

Run without --demo to start the real MCP stdio server.
```

## What it demonstrates

- `AddMcpAgentServerStdio()` — registers `StdioAgentServerHost` (`IHostedService`) that boots the MCP JSON-RPC channel on stdin/stdout when the host starts.
- `McpAgentServerBuilder` — wraps the registry dynamically: `tools/list` reflects the live state of `IAgentRegistry` on every call.
- One MCP tool per agent — name = `AgentManifest.Id`; input schema: `{ text, sessionId?, resume? }`.
- Resources endpoint — each agent manifest is exposed at `agent://{id}/{version}/manifest`.
- Console logging suppressed in server mode so stdout stays clean for the MCP JSON-RPC stream.

## Claude Desktop integration

Add to `~/.config/claude/claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "greeter": {
      "command": "dotnet",
      "args": ["run", "--project", "/absolute/path/to/McpServerStdio"]
    }
  }
}
```

Replace the scripted provider with a real `ICompletionProvider` (e.g. `OpenAiCompletionProvider`) and register any `IAgentManifest` entries you need before calling `host.RunAsync()`.

## Docs

- [Host agents as MCP tools](../../docs/guides/host-agents-as-mcp-tools.md)
- [`McpServerHttp`](../McpServerHttp) — same agent over streamable-HTTP instead of stdio
